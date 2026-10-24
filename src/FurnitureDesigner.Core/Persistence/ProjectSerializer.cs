using System.Text.Json;
using System.Text.Json.Serialization;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Persistence;

public static class ProjectSerializer
{
    public const string FileExtension = ".fdp";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(FurnitureProject project) => JsonSerializer.Serialize(project, Options);

    public static FurnitureProject Deserialize(string json) =>
        JsonSerializer.Deserialize<FurnitureProject>(json, Options)
        ?? throw new InvalidDataException("The file does not contain a furniture project.");

    public static void Save(FurnitureProject project, string path) => File.WriteAllText(path, Serialize(project));

    public static FurnitureProject Load(string path) => Deserialize(File.ReadAllText(path));
}
