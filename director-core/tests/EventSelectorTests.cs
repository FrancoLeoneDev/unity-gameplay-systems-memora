// Tests del EventSelector — plan §11.G (Selector_*).
// Lógica verificada por TDD red-green fuera de Unity; mismos casos en NUnit como gate del Test Runner.
using System.Collections.Generic;
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class EventSelectorTests
{
    private sealed class FakeEvent : ISelectableEvent
    {
        public int Weight { get; }
        public bool Eligible { get; set; } = true;
        public EventTier Tier { get; set; } = EventTier.AudioDistant;
        public DiegeticSource Source { get; set; } = DiegeticSource.House;
        public FakeEvent(int weight = 1) { Weight = weight; }
        public bool IsEligible(float now) => Eligible;
    }

    private sealed class FakeGate : IEventGate
    {
        private readonly HashSet<ISelectableEvent> _blocked;
        public FakeGate(params ISelectableEvent[] blocked) { _blocked = new HashSet<ISelectableEvent>(blocked); }
        public bool IsBlocked(ISelectableEvent ev, float now) => _blocked.Contains(ev);
    }

    [Test]
    public void Selector_ReturnsNullWhenAllIneligible()
    {
        var pool = new List<ISelectableEvent>
        {
            new FakeEvent { Eligible = false },
            new FakeEvent { Eligible = false },
            new FakeEvent { Eligible = false },
        };
        var sel = new EventSelector(pool, seed: 1);
        Assert.That(sel.SelectNext(0f), Is.Null, "Todo inelegible => silencio forzado (null), sin colgarse");
    }

    [Test]
    public void Selector_SkipsIneligible()
    {
        var a = new FakeEvent();
        var pool = new List<ISelectableEvent>
        {
            a,
            new FakeEvent { Eligible = false },
            new FakeEvent { Eligible = false },
        };
        var sel = new EventSelector(pool, seed: 2);
        for (int i = 0; i < 20; i++)
            Assert.That(sel.SelectNext(0f), Is.SameAs(a), "Solo debe devolver el elegible");
    }

    [Test]
    public void Selector_RespectsWeights()
    {
        var a = new FakeEvent(1);
        var b = new FakeEvent(3);
        var sel = new EventSelector(new List<ISelectableEvent> { a, b }, seed: 7);
        int ca = 0, cb = 0;
        for (int i = 0; i < 4000; i++) { if (ReferenceEquals(sel.SelectNext(0f), a)) ca++; else cb++; }
        double ratio = (double)cb / ca;
        Assert.That(ratio, Is.EqualTo(3.0).Within(0.4), "B/A ~ 3 por peso");
    }

    [Test]
    public void Selector_NoRepeatUntilCycle()
    {
        var pool = new List<ISelectableEvent> { new FakeEvent(), new FakeEvent(), new FakeEvent() };
        var sel = new EventSelector(pool, seed: 5);
        var seen = new HashSet<ISelectableEvent>();
        for (int i = 0; i < 3; i++) seen.Add(sel.SelectNext(0f));
        Assert.That(seen.Count, Is.EqualTo(3), "Un ciclo debe cubrir los 3 distintos");
    }

    [Test]
    public void Selector_BecomesEligibleAfterCooldown()
    {
        var a = new FakeEvent { Eligible = false };
        var sel = new EventSelector(new List<ISelectableEvent> { a }, seed: 3);
        Assert.That(sel.SelectNext(0f), Is.Null, "Inelegible => null");
        a.Eligible = true;
        Assert.That(sel.SelectNext(0f), Is.SameAs(a), "Vuelve a ser elegible (no se consumió al saltearlo)");
    }

    [Test]
    public void Selector_SkipsGateBlocked()
    {
        var a = new FakeEvent();
        var b = new FakeEvent();
        var sel = new EventSelector(new List<ISelectableEvent> { a, b }, seed: 11, new FakeGate(b));
        for (int i = 0; i < 20; i++)
            Assert.That(sel.SelectNext(0f), Is.SameAs(a), "El gate bloquea b => solo sale a (sin dead-air)");
    }

    [Test]
    public void Selector_ReturnsNullWhenGateBlocksAll()
    {
        var a = new FakeEvent();
        var sel = new EventSelector(new List<ISelectableEvent> { a }, seed: 12, new FakeGate(a));
        Assert.That(sel.SelectNext(0f), Is.Null, "Gate bloquea todo => silencio forzado, sin colgarse");
    }

    [Test]
    public void Selector_NullGate_NoFiltering()
    {
        var a = new FakeEvent();
        var sel = new EventSelector(new List<ISelectableEvent> { a }, seed: 13, null);
        Assert.That(sel.SelectNext(0f), Is.SameAs(a), "gate null => comportamiento original intacto");
    }
}
