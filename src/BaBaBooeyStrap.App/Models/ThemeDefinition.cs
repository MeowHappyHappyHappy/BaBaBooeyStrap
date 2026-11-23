namespace BaBaBooeyStrap.App.Models;

public sealed record ThemeDefinition(
    string Background,
    string Foreground,
    string Name = "Fluent",
    string Accent = "#FF0078D4",
    double Width = 500,
    double Height = 320,
    bool AllowTransparency = false,
    bool IsCustomLayout = false
);
