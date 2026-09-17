using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// Replays every Godot model tick trace (fixtures/godot/models/*_tick_fixture.fullprec.json) against the ported
    /// C# model and compares the return value and full summary after configure, after each setup op, and after every
    /// step: type-aware and bit-exact. Every captured model must resolve to a ported type (a missing one fails).
    /// After the last step the fixture's <c>round_trip</c> is replayed: <c>get_summary()</c> → a fresh instance prepared as
    /// the recorded description says (same config, bare, or the pristine ShipSystemsManager) → <c>apply_summary()</c> →
    /// <c>get_summary()</c>, comparing the source summary, the apply return and the restored summary bit-exactly. The
    /// section is read from the <c>.fullprec.json</c> companion (every model fixture carries it there), so no float
    /// tolerance is needed.
    /// Calls are mapped by reflection from the recorded GDScript call (<c>tick(delta, context)</c> → <c>Tick(double, …)</c>),
    /// with small hooks for the few recorded operations that are expressions rather than calls.
    /// </summary>
    public class ModelTickParityTests
    {
        const string ModelsDir = "godot/models";

        static readonly Assembly CoreAssembly = typeof(GdDict).Assembly;

        public static IEnumerable<string> FixtureNames()
        {
            string dir = Path.Combine(Fixtures.FixturesDir, "godot", "models");
            if (!Directory.Exists(Fixtures.FixturesDir)) yield break; // stripped checkout: nothing to replay
            foreach (string f in Directory.GetFiles(dir, "*_tick_fixture.fullprec.json").OrderBy(x => x, StringComparer.Ordinal))
                yield return Path.GetFileName(f).Replace("_tick_fixture.fullprec.json", "");
        }

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
        }

        [TearDown]
        public void TearDown() => CatalogRegistry.Clear();

        [TestCaseSource(nameof(FixtureNames))]
        public void TickTraceMatchesGodot(string model)
        {
            var fixture = Fixtures.ReadDict($"{ModelsDir}/{model}_tick_fixture.fullprec.json");
            Type type = FindModelType(model);
            Assert.IsNotNull(type, $"no SynapticSea.Core type named {PascalCase(model)} for fixture {model}");

            object instance = Activator.CreateInstance(type);
            Configure(model, type, instance, fixture);
            AssertSummary(instance, fixture.Get("summary_after_configure"), "after configure");

            foreach (object op in fixture.GetArrayOrEmpty("setup"))
            {
                var o = (GdDict)op;
                object ret = RunOp(model, instance, o);
                if (o.Has("summary_after")) AssertSummary(instance, o.Get("summary_after"), $"after setup op '{o.GetString("op")}'");
                if (o.Has("return") && o.Get("return") != null) AssertTree(o.Get("return"), ret, $"return of setup op '{o.GetString("op")}'");
            }

            foreach (object s in fixture.GetArrayOrEmpty("steps"))
            {
                var step = (GdDict)s;
                string where = $"step {step.GetInt("index")} (group {step.GetInt("group")})";
                foreach (object op in step.GetArrayOrEmpty("pre_ops")) RunOp(model, instance, (GdDict)op);

                double delta = double.Parse(step.GetString("delta_str"), NumberStyles.Float, CultureInfo.InvariantCulture);
                object ret = InvokeRecordedCall(instance, step.GetString("call"), delta, step.GetDictOrEmpty("args"));
                if (step.Has("return")) AssertTree(step.Get("return"), ret, $"{where} return");
                AssertSummary(instance, step.Get("summary"), where);
            }

            ReplayRoundTrip(model, type, instance, fixture);
        }

        static void ReplayRoundTrip(string model, Type type, object source, GdDict fixture)
        {
            GdDict rt = fixture.GetDict("round_trip");
            Assert.IsNotNull(rt, $"{model}: fixture has no round_trip section");
            string description = rt.GetString("description");

            object sourceSummary = Invoke(source, "GetSummary", Array.Empty<object>());
            AssertTree(rt.Get("source_summary"), sourceSummary, "round_trip source_summary");

            object fresh = Activator.CreateInstance(type);
            if (description.Contains("no configure") || description.Contains("no setup"))
            {
                // The smoke restores into a bare instance.
            }
            else if (model == "ship_systems_manager")
            {
                StringAssert.Contains("configure(load_definitions(), 0, 4242)", description);
                ConfigureShipSystems(type, fresh, 0L);
            }
            else
            {
                StringAssert.Contains("configure with the same config", description, $"{model}: unrecognised round_trip recipe");
                Configure(model, type, fresh, fixture);
            }

            Assert.IsInstanceOf<GdDict>(sourceSummary, "get_summary() must return a dictionary");
            object applied = Invoke(fresh, "ApplySummary", new object[] { ((GdDict)sourceSummary).DeepCopy() });
            AssertTree(rt.Get("apply_summary_return"), applied, "round_trip apply_summary return");

            object restored = Invoke(fresh, "GetSummary", Array.Empty<object>());
            AssertTree(rt.Get("restored_summary"), restored, "round_trip restored_summary");

            // Godot's own verdict (spoilage_state records a lossy restore) must agree with the port's.
            bool equal = TreeDiff.Compare(sourceSummary, restored).Count == 0;
            Assert.AreEqual(rt.GetBool("equal"), equal, "round_trip source == restored verdict");
        }

        // ------------------------------------------------------------------ construction and configure

        static Type FindModelType(string model)
        {
            string name = PascalCase(model);
            return CoreAssembly.GetTypes().FirstOrDefault(t => t.Name == name && t.Namespace != null && t.Namespace.StartsWith("SynapticSea.Core", StringComparison.Ordinal));
        }

        static void Configure(string model, Type type, object instance, GdDict fixture)
        {
            string call = fixture.GetString("configure_call");
            if (call.StartsWith("none", StringComparison.Ordinal)) return;
            if (model == "ship_systems_manager")
            {
                // configure(load_definitions(), 1, 4242)
                ConfigureShipSystems(type, instance, 1L);
                return;
            }
            var config = call.Contains("configure({})") ? new GdDict() : fixture.GetDictOrEmpty("config").DeepCopy();
            Invoke(instance, "Configure", new object[] { config });
        }

        static void ConfigureShipSystems(Type type, object instance, long condition)
        {
            var load = type.GetMethod("LoadDefinitions", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            Assert.IsNotNull(load, "ShipSystemsManager.LoadDefinitions not found");
            object defs = load.Invoke(load.IsStatic ? null : instance, Array.Empty<object>());
            Invoke(instance, "Configure", new object[] { defs, condition, 4242L });
        }

        // ------------------------------------------------------------------ ops and calls

        static object RunOp(string model, object instance, GdDict op)
        {
            string name = op.GetString("op");
            var args = op.GetDictOrEmpty("args");
            if (name.StartsWith("set ", StringComparison.Ordinal))
            {
                string member = name.Substring(4).Trim();
                SetMember(instance, member, args.Get(member));
                return null;
            }
            if (model == "ship_systems_manager" && name.Contains(".health = "))
            {
                // get_system("power").get_subcomponent("reactor_core").health = 0.0
                object system = Invoke(instance, "GetSystem", new object[] { args.GetString("system") });
                object sub = Invoke(system, "GetSubcomponent", new object[] { args.GetString("subcomponent") });
                SetMember(sub, "health", args.Get("health"));
                return null;
            }
            if (!Regex.IsMatch(name, @"^[a-z_][a-z0-9_]*$")) Assert.Fail($"unsupported recorded op '{name}' — add a hook");
            return InvokeNamed(instance, name, args);
        }

        static object InvokeRecordedCall(object instance, string call, double delta, GdDict args)
        {
            var m = Regex.Match(call, @"^(?<name>[a-z_][a-z0-9_]*)\((?<params>[^)]*)\)");
            Assert.IsTrue(m.Success, $"cannot parse recorded call '{call}'");
            string[] names = m.Groups["params"].Value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            var values = names.Select(n => n == "delta" ? (object)delta : args.Get(n)).ToArray();
            return Invoke(instance, PascalCase(m.Groups["name"].Value), values);
        }

        static object InvokeNamed(object instance, string gdName, GdDict args)
        {
            var candidates = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(mi => mi.Name == PascalCase(gdName)).ToList();
            Assert.IsNotEmpty(candidates, $"{instance.GetType().Name}.{PascalCase(gdName)} not found");
            foreach (var mi in candidates.OrderByDescending(c => c.GetParameters().Length))
            {
                var ps = mi.GetParameters();
                var values = new object[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    var match = args.Keys.Select(V.Str).FirstOrDefault(k => Norm(k) == Norm(ps[i].Name));
                    if (match != null) values[i] = Convert(args.Get(match), ps[i].ParameterType);
                    else if (ps[i].HasDefaultValue) values[i] = ps[i].DefaultValue;
                    else { ok = false; break; }
                }
                if (ok) return Unwrap(mi.Invoke(instance, values));
            }
            Assert.Fail($"no overload of {instance.GetType().Name}.{PascalCase(gdName)} accepts args [{string.Join(", ", args.Keys.Select(V.Str))}]");
            return null;
        }

        static object Invoke(object instance, string name, object[] args)
        {
            var methods = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(mi => mi.Name == name).ToList();
            Assert.IsNotEmpty(methods, $"{instance.GetType().Name}.{name} not found");
            foreach (var mi in methods.OrderBy(c => Math.Abs(c.GetParameters().Length - args.Length)))
            {
                var ps = mi.GetParameters();
                if (ps.Length < args.Length) continue;
                if (ps.Skip(args.Length).Any(p => !p.HasDefaultValue)) continue;
                var values = new object[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i < args.Length)
                    {
                        if (!CanConvert(args[i], ps[i].ParameterType)) { ok = false; break; }
                        values[i] = Convert(args[i], ps[i].ParameterType);
                    }
                    else values[i] = ps[i].DefaultValue;
                }
                if (ok) return Unwrap(mi.Invoke(mi.IsStatic ? null : instance, values));
            }
            Assert.Fail($"no overload of {instance.GetType().Name}.{name} fits {args.Length} argument(s)");
            return null;
        }

        static object Unwrap(object v)
        {
            if (v == null) return null;
            try { return V.Normalize(v); }
            catch (ArgumentException) { return v; }
        }

        static void SetMember(object instance, string gdName, object value)
        {
            var t = instance.GetType();
            string pascal = PascalCase(gdName);
            var prop = t.GetProperty(pascal, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(instance, Convert(value, prop.PropertyType));
                return;
            }
            var field = t.GetField(pascal, BindingFlags.Public | BindingFlags.Instance) ?? t.GetField(gdName, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{t.Name} has no writable member for '{gdName}'");
            field.SetValue(instance, Convert(value, field.FieldType));
        }

        static bool CanConvert(object v, Type target)
        {
            if (v == null) return !target.IsValueType || Nullable.GetUnderlyingType(target) != null;
            if (target == typeof(object) || target.IsInstanceOfType(v)) return true;
            if (target == typeof(double) || target == typeof(float) || target == typeof(long) || target == typeof(int)) return V.IsNumber(v);
            if (target == typeof(bool)) return v is bool;
            if (target == typeof(string)) return v is string;
            return false;
        }

        static object Convert(object v, Type target)
        {
            if (target == typeof(object)) return v;
            if (target == typeof(double)) return V.F64(v);
            if (target == typeof(float)) return (float)V.F64(v);
            if (target == typeof(long)) return V.I64(v);
            if (target == typeof(int)) return V.I32(v);
            if (target == typeof(bool)) return V.Bool(v);
            if (target == typeof(string)) return v == null ? null : V.Str(v);
            if (target.IsEnum) return Enum.ToObject(target, V.I64(v));
            return v;
        }

        // ------------------------------------------------------------------ assertions

        static void AssertSummary(object instance, object expected, string where)
        {
            object summary = Invoke(instance, "GetSummary", Array.Empty<object>());
            AssertTree(expected, summary, $"summary {where}");
        }

        static void AssertTree(object expected, object actual, string where)
        {
            var diffs = TreeDiff.Compare(expected, actual);
            if (diffs.Count > 0) Assert.Fail($"{where}: {TreeDiff.Format(diffs)}");
        }

        // ------------------------------------------------------------------ names

        static string PascalCase(string snake) =>
            string.Concat(snake.Split('_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));

        static string Norm(string name) => name.Replace("_", "").ToLowerInvariant();
    }
}
