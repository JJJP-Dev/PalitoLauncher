using PalitoLauncher.Core.Missions;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>Conjunto de misiones de prueba para los tests de UI: una completada, una pendiente, una con progreso y una manual.</summary>
internal static class TestMissions
{
    public const string Json = """
        { "id": "pre2-ui", "title": "Misiones del Pre 2", "intro": "Prueba esto en la sesión del sábado.",
          "missions": [
            { "id": "vapor", "title": "Provoca un Vapor en dúo", "check": "¿Se vio el titular?",
              "goal": { "event": "reaction", "where": { "variant": "vapor_*", "duo": true } } },
            { "id": "tarde", "title": "Únete a una partida ya empezada", "hint": "Pide a un amigo que hospede y entra a los dos minutos.",
              "goal": { "event": "match_joined", "where": { "late": true } } },
            { "id": "kills", "title": "Elimina 30 perseguidores",
              "goal": { "event": "enemy_killed", "where": { "by": "me" }, "count": 30, "scope": "set" } },
            { "id": "numeros", "title": "¿Se leen bien los números de daño?", "manual": true } ] }
        """;

    public static (MissionSet Set, MissionSetState State) Sample()
    {
        var set = MissionsProvider.Parse(Json) ?? throw new InvalidOperationException("missions.json de prueba no válido");
        var state = new MissionSetState { SetId = set.Id, CreatedUtc = TestManifest.Now.AddDays(-1), SessionsCounted = 1 };
        var vapor = state.GetOrCreate(set.Missions[0]);
        vapor.State = MissionState.Done;
        vapor.Automatic = true;
        vapor.CompletedUtc = TestManifest.Now.AddHours(-2);
        state.GetOrCreate(set.Missions[2]).Parts[0].Progress = 12;
        return (set, state);
    }
}
