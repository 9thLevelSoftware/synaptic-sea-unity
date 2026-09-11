// Ported from scripts/systems/localization_catalog.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-RL-005 localization catalog. <c>{language_id: {string_id: translation}}</c> with deterministic
    /// fallbacks: unknown string id -> "" (or the default text), unknown language / missing key ->
    /// the default language's text.
    /// </summary>
    public class LocalizationCatalog
    {
        public const string DefaultLanguage = "en";

        readonly GdDict _catalog = new GdDict();
        string _defaultLanguage = DefaultLanguage;
        readonly GdArray _knownLanguages = new GdArray();

        public void Configure(GdDict catalog, string defaultLanguage = DefaultLanguage)
        {
            _catalog.Clear();
            _knownLanguages.Clear();
            if (catalog == null) catalog = new GdDict();
            _defaultLanguage = !string.IsNullOrEmpty(defaultLanguage) ? defaultLanguage : DefaultLanguage;
            foreach (var langIdVariant in catalog.Keys)
            {
                string langId = V.Str(langIdVariant);
                if (langId.Length == 0) continue;
                object langDictVariant = catalog[langIdVariant];
                if (!(langDictVariant is GdDict source)) continue;
                _knownLanguages.Add(langId);
                var langDict = new GdDict();
                foreach (var stringIdVariant in source.Keys)
                {
                    string stringId = V.Str(stringIdVariant);
                    langDict[stringId] = V.Str(source[stringIdVariant]);
                }
                _catalog[langId] = langDict;
            }
            if (!_knownLanguages.Contains(_defaultLanguage))
            {
                _knownLanguages.Add(_defaultLanguage);
                if (!_catalog.Has(_defaultLanguage)) _catalog[_defaultLanguage] = new GdDict();
            }
        }

        public string Translate(string stringId, string languageId)
        {
            if (string.IsNullOrEmpty(stringId)) return "";
            if (_catalog.Has(languageId))
            {
                var langDict = (GdDict)_catalog[languageId];
                if (langDict.Has(stringId)) return V.Str(langDict[stringId]);
            }
            // Fall back to default language.
            if (_catalog.Has(_defaultLanguage))
            {
                var defaultDict = (GdDict)_catalog[_defaultLanguage];
                if (defaultDict.Has(stringId)) return V.Str(defaultDict[stringId]);
            }
            return "";
        }

        public string TranslateFallback(string stringId, string defaultText, string languageId)
        {
            if (string.IsNullOrEmpty(stringId)) return defaultText;
            string translated = Translate(stringId, languageId);
            if (translated.Length == 0) return defaultText;
            return translated;
        }

        public bool HasTranslation(string stringId, string languageId)
        {
            if (!_catalog.Has(languageId)) return false;
            var langDict = (GdDict)_catalog[languageId];
            return langDict.Has(stringId);
        }

        public GdArray GetKnownLanguages() => _knownLanguages.ShallowCopy();

        public string GetDefaultLanguage() => _defaultLanguage;

        public long GetTranslationCount()
        {
            long count = 0;
            foreach (var langId in _knownLanguages)
            {
                if (_catalog.Has(langId)) count += ((GdDict)_catalog[langId]).Count;
            }
            return count;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "default_language", _defaultLanguage },
                { "known_languages", _knownLanguages.ShallowCopy() },
                { "translation_count", GetTranslationCount() },
            };
        }
    }
}
