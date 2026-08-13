// Tests del ShuffleBag<T> del Director de Eventos Ambientales — plan §11.G.
// Lógica verificada por TDD red-green fuera de Unity (dotnet) antes de integrarse;
// estos son los mismos casos en NUnit como gate del Test Runner del editor.
// Sintaxis NUnit (Assert.That(...)); NO UnityEngine.Assertions.
using System.Collections.Generic;
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class ShuffleBagTests
{
    // Bolsa A,B,C (peso 1 c/u): un ciclo (3 draws) contiene cada item exactamente una vez.
    [Test]
    public void ShuffleBag_NeverRepeatsUntilCycle()
    {
        var bag = new ShuffleBag<string>(seed: 1);
        bag.Add("A"); bag.Add("B"); bag.Add("C");

        var counts = new Dictionary<string, int> { ["A"] = 0, ["B"] = 0, ["C"] = 0 };
        for (int i = 0; i < 3; i++) counts[bag.Next()]++;

        Assert.That(counts["A"], Is.EqualTo(1));
        Assert.That(counts["B"], Is.EqualTo(1));
        Assert.That(counts["C"], Is.EqualTo(1));
    }

    // Misma seed + mismos adds => misma secuencia.
    [Test]
    public void ShuffleBag_SameSeedSameOrder()
    {
        var a = new ShuffleBag<int>(seed: 42); a.Add(1); a.Add(2); a.Add(3); a.Add(4);
        var b = new ShuffleBag<int>(seed: 42); b.Add(1); b.Add(2); b.Add(3); b.Add(4);

        for (int i = 0; i < 20; i++)
            Assert.That(a.Next(), Is.EqualTo(b.Next()), "Divergencia en el draw " + i);
    }

    // Peso = multiplicidad: B (peso 3) sale ~3x más que A (peso 1).
    [Test]
    public void ShuffleBag_WeightedProportional()
    {
        var bag = new ShuffleBag<string>(seed: 7);
        bag.Add("A", 1); bag.Add("B", 3);

        int a = 0, b = 0;
        for (int i = 0; i < 4000; i++) { if (bag.Next() == "A") a++; else b++; }

        double ratio = (double)b / a;
        Assert.That(ratio, Is.EqualTo(3.0).Within(0.4), "Proporción B/A fuera de rango");
    }

    // Bolsa de 1 elemento: devuelve siempre el item, sin loop infinito (bug do-while RandomSoundsManager:35).
    [Test]
    public void ShuffleBag_SizeOneNoSpin()
    {
        var bag = new ShuffleBag<string>(seed: 3);
        bag.Add("solo");

        for (int i = 0; i < 5; i++)
            Assert.That(bag.Next(), Is.EqualTo("solo"));
    }

    // El primer draw de un ciclo nuevo != último draw del ciclo anterior (con >= 2 distintos).
    [Test]
    public void ShuffleBag_NoRepeatAcrossReshuffle()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var bag = new ShuffleBag<int>(seed);
            bag.Add(1); bag.Add(2); bag.Add(3); bag.Add(4); // count=4 por ciclo

            int prev = bag.Next();
            for (int i = 1; i < 40; i++)
            {
                int cur = bag.Next();
                if (i % 4 == 0) // borde de reshuffle (item 0 del ciclo nuevo)
                    Assert.That(cur, Is.Not.EqualTo(prev),
                        "Repetición en el borde de reshuffle (seed " + seed + ", draw " + i + ")");
                prev = cur;
            }
        }
    }
}

[TestFixture]
public class FakeDirectorClockTests
{
    [Test]
    public void FakeDirectorClock_Advance_AddsToNow()
    {
        var clock = new FakeDirectorClock(start: 10f);
        clock.Advance(2.5f);
        Assert.That(clock.Now, Is.EqualTo(12.5f).Within(0.0001f));
    }

    [Test]
    public void FakeDirectorClock_Advance_Negative_Throws()
    {
        var clock = new FakeDirectorClock();
        Assert.That(() => clock.Advance(-1f), Throws.TypeOf<System.ArgumentOutOfRangeException>());
    }
}
