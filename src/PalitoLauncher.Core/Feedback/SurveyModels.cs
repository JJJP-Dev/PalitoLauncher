namespace PalitoLauncher.Core.Feedback;

public enum SurveyQuestionType
{
    /// <summary>Valoración con estrellas 1..5.</summary>
    Rating,

    /// <summary>Escala numérica Min..Max (por defecto 1..5) con etiquetas opcionales en los extremos.</summary>
    Scale,

    /// <summary>Una opción de la lista.</summary>
    Choice,

    /// <summary>Varias opciones de la lista.</summary>
    Multi,

    /// <summary>Texto libre (máx. MaxLength caracteres).</summary>
    Text,
}

/// <summary>Pregunta de la encuesta. En JSON los nombres van en camelCase y el tipo como texto ("rating", "scale"…).</summary>
public sealed class SurveyQuestion
{
    /// <summary>Identificador estable (letras, números, '-' y '_'), p.ej. "overall". Sirve para comparar respuestas entre versiones.</summary>
    public string Id { get; set; } = "";

    public SurveyQuestionType Type { get; set; }

    /// <summary>Texto de la pregunta (máx. 200 caracteres).</summary>
    public string Text { get; set; } = "";

    /// <summary>Ayuda opcional bajo la pregunta (máx. 200).</summary>
    public string? Hint { get; set; }

    public bool Required { get; set; }

    /// <summary>Opciones para Choice/Multi (2..10, cada una máx. 80 caracteres).</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>Escala: mínimo y máximo (por defecto 1 y 5; rango permitido 0..10).</summary>
    public int Min { get; set; } = 1;

    public int Max { get; set; } = 5;

    /// <summary>Escala: etiquetas de los extremos (p.ej. "Muy fácil" / "Muy difícil").</summary>
    public string? MinLabel { get; set; }

    public string? MaxLabel { get; set; }

    /// <summary>Texto: longitud máxima (por defecto 1000; máx. 1000 por el límite de Discord).</summary>
    public int MaxLength { get; set; } = 1000;

    public string? Placeholder { get; set; }
}

/// <summary>Definición completa de la encuesta (por defecto o desde launcher/feedback.json del repo del juego).</summary>
public sealed class SurveyDefinition
{
    /// <summary>Identificador de la encuesta (p.ej. "pre2-fun-test"); cámbialo cuando cambies las preguntas.</summary>
    public string Id { get; set; } = "default";

    public string Title { get; set; } = "";

    /// <summary>Texto introductorio corto (máx. 300).</summary>
    public string? Intro { get; set; }

    /// <summary>Si es false, no se invita a responder al cerrar el juego (el botón manual sigue funcionando).</summary>
    public bool AskAfterSession { get; set; } = true;

    /// <summary>Minutos mínimos de partida para invitar a responder al cerrar el juego (1..120, por defecto 5).</summary>
    public int MinSessionMinutes { get; set; } = 5;

    /// <summary>Horas mínimas entre dos invitaciones automáticas (0..168, por defecto 12).</summary>
    public int MinHoursBetweenPrompts { get; set; } = 12;

    /// <summary>
    /// Webhook de Discord donde se publican las respuestas (https://discord.com/api/webhooks/… o discordapp.com). Si falta
    /// o no es válido, las respuestas se guardan en la bandeja de salida y se envían cuando haya uno.
    /// </summary>
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>1..12 preguntas.</summary>
    public List<SurveyQuestion> Questions { get; set; } = new();
}

/// <summary>Una respuesta: Number para Rating/Scale, Choices para Choice/Multi, Text para Text. Null/vacío = sin responder.</summary>
public sealed class SurveyAnswer
{
    public int? Number { get; set; }
    public List<string>? Choices { get; set; }
    public string? Text { get; set; }

    public bool IsEmpty => Number is null && (Choices is null || Choices.Count == 0) && string.IsNullOrWhiteSpace(Text);
}

public enum FeedbackTrigger
{
    /// <summary>Invitación automática al cerrar el juego.</summary>
    AfterSession,

    /// <summary>El usuario pulsó «Enviar opinión».</summary>
    Manual,
}

/// <summary>Una respuesta completa lista para enviar (se guarda tal cual en la bandeja de salida).</summary>
public sealed class FeedbackSubmission
{
    /// <summary>Identificador único (Guid "N") para no enviar dos veces la misma respuesta.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string SurveyId { get; set; } = "";
    public string SurveyTitle { get; set; } = "";

    /// <summary>Preguntas tal como se mostraron (para formatear el mensaje aunque luego cambie la encuesta).</summary>
    public List<SurveyQuestion> Questions { get; set; } = new();

    /// <summary>Respuestas por Id de pregunta.</summary>
    public Dictionary<string, SurveyAnswer> Answers { get; set; } = new();

    public string? Nickname { get; set; }
    public FeedbackTrigger Trigger { get; set; }

    /// <summary>Versión del juego instalada (GameVersion.ToString()) y su texto para la UI.</summary>
    public string? GameVersion { get; set; }

    public string? GameVersionDisplay { get; set; }
    public string LauncherVersion { get; set; } = "";
    public string? OsDescription { get; set; }

    /// <summary>Duración de la partida en minutos (si viene de AfterSession).</summary>
    public int? SessionMinutes { get; set; }

    /// <summary>Código de salida del juego (si viene de AfterSession).</summary>
    public int? ExitCode { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum FeedbackSendOutcome
{
    /// <summary>Publicada en Discord.</summary>
    Sent,

    /// <summary>Guardada en la bandeja de salida (sin conexión, sin webhook, rate limit…); se reintentará.</summary>
    Queued,
}
