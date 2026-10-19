using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.State;

namespace PalitoLauncher.Tests;

public sealed class RoadmapLogicTests
{
    private static MilestoneInfo M(int number, string title = "", int closed = 0, int open = 0, bool isClosed = false,
        string? description = null) => new()
    {
        Number = number,
        Title = title.Length == 0 ? $"Hito {number}" : title,
        ClosedIssues = closed,
        OpenIssues = open,
        IsClosed = isClosed,
        Description = description,
        HtmlUrl = $"https://github.com/JJJP-Dev/Coop2D/milestone/{number}",
    };

    private static MilestoneStatus[] Statuses(RoadmapSnapshot snapshot) => snapshot.Items.Select(i => i.Status).ToArray();

    // ───────────── Datos reales de Coop2D ─────────────

    [Fact]
    public void Build_RealCoop2DMilestones()
    {
        var milestones = GitHubClient.ParseMilestones(Coop2DMilestonesJson.Json());

        var snapshot = RoadmapLogic.Build(milestones);

        Assert.Equal(14, snapshot.TotalCount);
        Assert.Equal(0, snapshot.DoneCount);
        Assert.Equal(Enumerable.Range(1, 14), snapshot.Items.Select(i => i.Milestone.Number));

        // Pre 1 (26/27) y Pre 2 (14/25) a la vez en curso; Pre 3 (0/24) es el siguiente; el resto, planificado.
        var expected = new[] { MilestoneStatus.InProgress, MilestoneStatus.InProgress, MilestoneStatus.Next }
            .Concat(Enumerable.Repeat(MilestoneStatus.Planned, 11));
        Assert.Equal(expected, Statuses(snapshot));
        Assert.Equal(new[] { "Pre 1 · Tech prototype", "Pre 2 · Gameplay prototype" }, snapshot.Current.Select(i => i.Milestone.Title));

        Assert.All(snapshot.Items.Take(4), i => Assert.Equal("Pre-producción", i.Phase));
        Assert.All(snapshot.Items.Skip(4).Take(9), i => Assert.Equal("Producción", i.Phase));
        Assert.Equal("Lanzamiento", snapshot.Items[13].Phase);

        Assert.Equal("¿Pueden 2-4 jugadores conectarse por internet y moverse, con el host como autoridad?", snapshot.Items[0].Summary);
        Assert.Equal("¿Es divertido combinar elementos con tus compañeros?", snapshot.Items[1].Summary);
        Assert.Equal("Partidas formadas por salas con enemigos, cofres y una salida.", snapshot.Items[4].Summary);
        Assert.Equal("Publicar Coop2D.", snapshot.Items[13].Summary);

        Assert.Equal("26 de 27 tareas · 96 %", snapshot.Items[0].ProgressText);
        Assert.Equal("14 de 25 tareas · 56 %", snapshot.Items[1].ProgressText);
        Assert.Equal("0 de 24 tareas · 0 %", snapshot.Items[2].ProgressText);
        Assert.Equal("Sin tareas todavía", snapshot.Items[3].ProgressText);
    }

    [Fact]
    public void Build_RealData_AfterClosingPre1()
    {
        var milestones = GitHubClient.ParseMilestones(Coop2DMilestonesJson.Json(n => n == 1 ? "closed" : "open"));

        var snapshot = RoadmapLogic.Build(milestones);

        Assert.Equal(1, snapshot.DoneCount);
        Assert.Equal(MilestoneStatus.Done, snapshot.Items[0].Status);
        Assert.Equal("Completado", snapshot.Items[0].ProgressText);
        Assert.Equal("Pre 2 · Gameplay prototype", Assert.Single(snapshot.Current).Milestone.Title);
        Assert.Equal(MilestoneStatus.Next, snapshot.Items[2].Status);
    }

    // ───────────── Clasificación ─────────────

    [Fact]
    public void Build_SortsByNumberWhateverTheInputOrder()
    {
        var snapshot = RoadmapLogic.Build(new[] { M(3), M(1, closed: 2, open: 1), M(2) });

        Assert.Equal(new[] { 1, 2, 3 }, snapshot.Items.Select(i => i.Milestone.Number));
        Assert.Equal(new[] { MilestoneStatus.InProgress, MilestoneStatus.Next, MilestoneStatus.Planned }, Statuses(snapshot));
    }

    [Fact]
    public void Build_NoneInProgress_NextIsFirstOpen_AndIsCurrent()
    {
        var snapshot = RoadmapLogic.Build(new[]
        {
            M(1, closed: 10, isClosed: true),
            M(2, closed: 0, open: 3, isClosed: true), // cerrado aunque tenga tareas abiertas → Done
            M(3, open: 5),
            M(4, open: 2),
        });

        Assert.Equal(new[] { MilestoneStatus.Done, MilestoneStatus.Done, MilestoneStatus.Next, MilestoneStatus.Planned }, Statuses(snapshot));
        Assert.Equal(3, Assert.Single(snapshot.Current).Milestone.Number);
        Assert.Equal(2, snapshot.DoneCount);
        Assert.Equal(4, snapshot.TotalCount);
    }

    [Fact]
    public void Build_OpenWithoutProgressBeforeLastInProgress_IsPlanned()
    {
        var snapshot = RoadmapLogic.Build(new[]
        {
            M(1, open: 3),             // abierto sin tareas cerradas, pero antes del último en curso
            M(2, closed: 1, open: 3),  // en curso
            M(3),                      // sin tareas: primero tras el último en curso → Next
            M(4, open: 7),
        });

        Assert.Equal(new[] { MilestoneStatus.Planned, MilestoneStatus.InProgress, MilestoneStatus.Next, MilestoneStatus.Planned },
            Statuses(snapshot));
        Assert.Equal(2, Assert.Single(snapshot.Current).Milestone.Number);
    }

    [Fact]
    public void Build_ClosedMilestonesBetweenInProgress_AreSkippedForNext()
    {
        var snapshot = RoadmapLogic.Build(new[]
        {
            M(1, closed: 1, open: 1),
            M(2, isClosed: true),
            M(3, closed: 4),          // en curso (todo cerrado pero el milestone sigue abierto)
            M(4, isClosed: true),
            M(5),
        });

        Assert.Equal(new[] { MilestoneStatus.InProgress, MilestoneStatus.Done, MilestoneStatus.InProgress, MilestoneStatus.Done, MilestoneStatus.Next },
            Statuses(snapshot));
        Assert.Equal(new[] { 1, 3 }, snapshot.Current.Select(i => i.Milestone.Number));
    }

    [Fact]
    public void Build_AllClosed_HasNoNextAndEmptyCurrent()
    {
        var snapshot = RoadmapLogic.Build(new[] { M(1, closed: 3, isClosed: true), M(2, isClosed: true) });

        Assert.All(snapshot.Items, i => Assert.Equal(MilestoneStatus.Done, i.Status));
        Assert.Empty(snapshot.Current);
        Assert.Equal(2, snapshot.DoneCount);
        Assert.Equal(2, snapshot.TotalCount);
    }

    [Fact]
    public void Build_AllInProgress_HasNoNext()
    {
        var snapshot = RoadmapLogic.Build(new[] { M(1, closed: 1, open: 1), M(2, closed: 2, open: 2) });

        Assert.DoesNotContain(snapshot.Items, i => i.Status == MilestoneStatus.Next);
        Assert.Equal(2, snapshot.Current.Count);
    }

    [Fact]
    public void Build_SingleOpenMilestone_IsNextAndCurrent()
    {
        var snapshot = RoadmapLogic.Build(new[] { M(1) });

        Assert.Equal(MilestoneStatus.Next, Assert.Single(snapshot.Items).Status);
        Assert.Same(snapshot.Items[0], Assert.Single(snapshot.Current));
    }

    [Fact]
    public void Build_NullOrEmpty_ReturnsEmptySnapshot()
    {
        foreach (var input in new IEnumerable<MilestoneInfo>?[] { null, Array.Empty<MilestoneInfo>(), new MilestoneInfo[] { null! } })
        {
            var snapshot = RoadmapLogic.Build(input);
            Assert.Empty(snapshot.Items);
            Assert.Empty(snapshot.Current);
            Assert.Equal(0, snapshot.DoneCount);
            Assert.Equal(0, snapshot.TotalCount);
        }
    }

    [Fact]
    public void Build_SkipsNullItems_AndToleratesNullTitleAndDescription()
    {
        var weird = new MilestoneInfo { Number = 2, Title = null!, Description = null, ClosedIssues = -5, OpenIssues = -1 };

        var snapshot = RoadmapLogic.Build(new[] { null!, M(1, closed: 1), weird });

        Assert.Equal(2, snapshot.TotalCount);
        var item = snapshot.Items[1];
        Assert.Equal(MilestoneStatus.Next, item.Status); // tareas negativas = ninguna cerrada
        Assert.Equal("Producción", item.Phase);
        Assert.Null(item.Summary);
        Assert.Equal("Sin tareas todavía", item.ProgressText);
    }

    [Fact]
    public void Build_LazyEnumerable_IsEnumeratedOnce()
    {
        var count = 0;
        IEnumerable<MilestoneInfo> Source()
        {
            count++;
            yield return M(2);
            yield return M(1, closed: 1);
        }

        var snapshot = RoadmapLogic.Build(Source());

        Assert.Equal(1, count);
        Assert.Equal(2, snapshot.TotalCount);
    }

    [Fact]
    public void Build_EnumerableThatThrows_ReturnsEmptySnapshot()
    {
        static IEnumerable<MilestoneInfo> Broken()
        {
            yield return new MilestoneInfo { Number = 1 };
            throw new InvalidOperationException("colección rota");
        }

        var snapshot = RoadmapLogic.Build(Broken());

        Assert.Empty(snapshot.Items);
        Assert.Equal(0, snapshot.TotalCount);
    }

    // ───────────── Fase ─────────────

    [Theory]
    [InlineData("Pre 1 · Tech prototype", "Pre-producción")]
    [InlineData("Pre 4 · Vertical slice", "Pre-producción")]
    [InlineData("pre-alpha", "Pre-producción")]
    [InlineData("Pre2", "Pre-producción")]
    [InlineData("PRE 3", "Pre-producción")]
    [InlineData("Preproducción", "Pre-producción")]
    [InlineData("Pre-production", "Pre-producción")]
    [InlineData("Pre", "Pre-producción")]
    [InlineData("Presentación final", "Producción")]
    [InlineData("Prestige system", "Producción")]
    [InlineData("0.1 · Runs and rooms", "Producción")]
    [InlineData("0.5 · Beta · CBT 1", "Producción")]
    [InlineData("0.9 · Private beta · Localization", "Producción")]
    [InlineData("1.0 · Launch", "Lanzamiento")]
    [InlineData("1.1 · Post-launch", "Lanzamiento")]
    [InlineData("v2.0 · DLC", "Lanzamiento")]
    [InlineData("10.3", "Lanzamiento")]
    [InlineData("Launch", "Producción")]
    [InlineData("2026 roadmap", "Producción")]
    [InlineData("", "Producción")]
    public void Phase_IsDerivedFromTitle(string title, string expected)
    {
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, title.Length == 0 ? " " : title) }).Items);
        Assert.Equal(expected, item.Phase);
    }

    // ───────────── Resumen ─────────────

    [Theory]
    [InlineData(Coop2DMilestonesJson.Pre1Description,
        "¿Pueden 2-4 jugadores conectarse por internet y moverse, con el host como autoridad?")]
    [InlineData(Coop2DMilestonesJson.RunsAndRoomsDescription, "Partidas formadas por salas con enemigos, cofres y una salida.")]
    [InlineData("Producción.\r\n\r\nObjetivo: salas con windows\r\n\r\nHecho cuando: …", "Salas con windows")]
    [InlineData("**Objetivo:** llegar a la versión 0.1 con todo jugable. Y algo más.", "Llegar a la versión 0.1 con todo jugable.")]
    [InlineData("**Pregunta**: ¿se entiende `detonar` sin __tutorial__?", "¿Se entiende detonar sin tutorial?")]
    [InlineData("pregunta:   ¿funciona   con   espacios?", "¿Funciona con espacios?")]
    [InlineData("Pre-producción.\n\nPregunta:\n¿la respuesta va en la línea siguiente?\n", "¿La respuesta va en la línea siguiente?")]
    [InlineData("Objetivo: 4 jugadores... sin lag.", "4 jugadores...")]
    [InlineData("Objetivo: «Partidas» rápidas", "«Partidas» rápidas")]
    [InlineData("Objetivo: ñandú.", "Ñandú.")]
    public void Summary_IsTextAfterLabel(string description, string expected)
    {
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, description: description) }).Items);
        Assert.Equal(expected, item.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("Pre-producción.\n\nHecho cuando: 4 jugadores se mueven.")]
    [InlineData("Objetivo:")]
    [InlineData("Objetivo:   \n\nHecho cuando: …")]
    [InlineData("Objetivo: **")]
    [InlineData("Los objetivos: ninguno")]
    public void Summary_IsNullWithoutUsefulLabel(string? description)
    {
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, description: description) }).Items);
        Assert.Null(item.Summary);
    }

    [Fact]
    public void Summary_FirstLabelWins()
    {
        var item = Assert.Single(RoadmapLogic.Build(new[]
        {
            M(1, description: "Objetivo: el primero.\n\nPregunta: ¿el segundo?"),
        }).Items);
        Assert.Equal("El primero.", item.Summary);
    }

    [Fact]
    public void Summary_LongText_IsCutAtWordWithEllipsis()
    {
        var words = string.Join(" ", Enumerable.Repeat("palabra", 40)); // 319 caracteres, sin punto
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, description: "Objetivo: " + words) }).Items);

        Assert.NotNull(item.Summary);
        Assert.True(item.Summary.Length <= 160, $"Longitud {item.Summary.Length}");
        Assert.EndsWith("palabra…", item.Summary);
        Assert.StartsWith("Palabra palabra", item.Summary);
        Assert.DoesNotContain("  ", item.Summary);
    }

    [Fact]
    public void Summary_LongWordWithoutSpaces_IsHardCut()
    {
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, description: "Objetivo: " + new string('a', 400)) }).Items);

        Assert.Equal(160, item.Summary!.Length);
        Assert.EndsWith("a…", item.Summary);
    }

    [Fact]
    public void Summary_ExactlyMaxLength_IsNotCut()
    {
        var text = "A" + new string('b', 158) + "."; // 160 caracteres
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, description: "Objetivo: " + text) }).Items);

        Assert.Equal(text, item.Summary);
    }

    [Fact]
    public void Summary_DoesNotSplitSurrogatePairs()
    {
        var text = new string('x', 158) + "🎮🎮🎮 fin";
        var item = Assert.Single(RoadmapLogic.Build(new[] { M(1, description: "Objetivo: " + text) }).Items);

        Assert.True(item.Summary!.Length <= 160);
        Assert.False(char.IsHighSurrogate(item.Summary[^2]), "No debe quedar medio emoji antes del «…»");
        Assert.EndsWith("…", item.Summary);
    }

    // ───────────── Texto de progreso ─────────────

    [Theory]
    [InlineData(14, 11, false, "14 de 25 tareas · 56 %")]
    [InlineData(1, 0, false, "1 de 1 tarea · 100 %")]
    [InlineData(0, 1, false, "0 de 1 tarea · 0 %")]
    [InlineData(0, 0, false, "Sin tareas todavía")]
    [InlineData(0, 24, false, "0 de 24 tareas · 0 %")]
    [InlineData(26, 1, false, "26 de 27 tareas · 96 %")]
    [InlineData(199, 1, false, "199 de 200 tareas · 99 %")] // nunca "100 %" con tareas abiertas
    [InlineData(29, 71, false, "29 de 100 tareas · 29 %")]  // 0.29 * 100 en double sería 28.999…
    [InlineData(2, 1, false, "2 de 3 tareas · 66 %")]
    [InlineData(-3, 4, false, "0 de 4 tareas · 0 %")]
    [InlineData(5, 3, true, "Completado")]
    [InlineData(0, 0, true, "Completado")]
    public void FormatProgress_Texts(int closed, int open, bool isClosed, string expected)
    {
        var milestone = M(1, closed: closed, open: open, isClosed: isClosed);

        Assert.Equal(expected, RoadmapLogic.FormatProgress(milestone));
        Assert.Equal(expected, new RoadmapItem(milestone, MilestoneStatus.InProgress, "Producción", null).ProgressText);
    }

    [Fact]
    public void FormatProgress_Null_DoesNotThrow()
    {
        Assert.Equal("Sin tareas todavía", RoadmapLogic.FormatProgress(null!));
    }
}
