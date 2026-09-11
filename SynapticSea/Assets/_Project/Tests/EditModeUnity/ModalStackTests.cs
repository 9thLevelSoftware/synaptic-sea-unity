using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.UI;

namespace SynapticSea.Tests.Unity
{
    public class ModalStackTests
    {
        sealed class Probe : IInputConsumer
        {
            public readonly List<UiCommand> Received = new List<UiCommand>();
            public string Token = "";
            public string Restored;
            public bool Covered;
            public Probe(string id, SurfaceTime time) { SurfaceId = id; Time = time; }
            public string SurfaceId { get; }
            public SurfaceTime Time { get; }
            public bool Consume(UiCommand command)
            {
                Received.Add(command);
                return true;
            }
            public string CaptureFocusToken() => Token;
            public void RestoreFocus(string token) => Restored = token;
            public void SetCovered(bool covered) => Covered = covered;
        }

        [Test]
        public void OnlyTheTopSurfaceConsumesAndGameplayIsBlocked()
        {
            var stack = new ModalStack();
            Assert.IsFalse(stack.BlocksGameplay);
            Assert.IsTrue(stack.IsGameplayActionAllowed("attack_primary"));
            var inventory = new Probe("inventory", SurfaceTime.Live);
            var pause = new Probe("menu", SurfaceTime.Paused);
            stack.Push(inventory);
            Assert.IsTrue(stack.BlocksGameplay);
            Assert.IsFalse(stack.SimulationPaused, "inspection keeps simulation live");
            stack.Push(pause);
            Assert.IsTrue(stack.SimulationPaused, "pause above inspection suspends simulation");
            Assert.IsTrue(inventory.Covered);
            stack.Dispatch(UiCommand.Down);
            CollectionAssert.IsEmpty(inventory.Received, "a covered surface never processes input");
            CollectionAssert.AreEqual(new[] { UiCommand.Down }, pause.Received);
            Assert.IsFalse(stack.IsGameplayActionAllowed("move_forward"));
        }

        [Test]
        public void PopRestoresTheCapturedFocusTokenAndSwallowsAHeldAccept()
        {
            var stack = new ModalStack();
            var inventory = new Probe("inventory", SurfaceTime.Live) { Token = "inv-self:scrap_metal" };
            var pause = new Probe("menu", SurfaceTime.Paused);
            stack.Push(inventory);
            stack.Push(pause);
            stack.Pop(pause);
            Assert.IsFalse(inventory.Covered);
            Assert.AreEqual("inv-self:scrap_metal", inventory.Restored);
            Assert.IsTrue(stack.Dispatch(UiCommand.Accept), "held accept is swallowed");
            CollectionAssert.IsEmpty(inventory.Received);
            stack.NotifyAcceptReleased();
            stack.Dispatch(UiCommand.Accept);
            CollectionAssert.AreEqual(new[] { UiCommand.Accept }, inventory.Received);
        }

        [Test]
        public void CompetingPanelTogglesAreRefusedWhileAnotherSurfaceIsOnTop()
        {
            var stack = new ModalStack();
            Assert.IsTrue(stack.IsPanelToggleAllowed("scanner"));
            stack.Push(new Probe("inventory", SurfaceTime.Live));
            Assert.IsTrue(stack.IsPanelToggleAllowed("inventory"), "its own toggle closes it");
            Assert.IsFalse(stack.IsPanelToggleAllowed("scanner"));
        }
    }
}
