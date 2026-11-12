using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using HuaweiPods.Protocol;

namespace HuaweiPods.Artwork;

internal sealed record OfficialArtworkVariant(DeviceIdentity Identity, string Description, bool IsCatalogDefault);

internal static class OfficialArtwork
{
    private const int MaxCatalogBytes = 512 * 1024;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private static readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Official");

    public static string? GetCatalogModelId(HeadsetProfile profile) =>
        profile.Model == HeadsetModel.FreeBuds3 ? "000027" : profile.ModelId;

    public static async Task<IReadOnlyList<OfficialArtworkVariant>> GetBundledVariantsAsync(
        string modelId, CancellationToken cancellationToken = default)
    {
        var catalog = await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return catalog.Values.Select(entry => entry.Variant)
            .Where(variant => variant.Identity.ModelId == modelId)
            .OrderBy(variant => variant.Identity.SubModelId, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task<string?> ResolveAsync(DeviceIdentity? identity, string? fallbackModelId,
        CancellationToken cancellationToken = default)
    {
        string? modelId = IsCatalogModelId(identity?.ModelId) ? identity?.ModelId : fallbackModelId;
        if (!IsCatalogModelId(modelId))
            return null;

        var catalog = await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (identity is not null && catalog.TryGetValue(identity, out var exact)
            && await ValidateAsync(exact, cancellationToken).ConfigureAwait(false) is { } exactPath)
            return exactPath;

        var candidates = catalog.Values
            .Where(entry => entry.Variant.Identity.ModelId == modelId && entry.Variant.Identity != identity)
            .OrderByDescending(entry => entry.Variant.Description is "白色产品资源" or "白色")
            .ThenBy(entry => entry.Variant.Identity.SubModelId, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (await ValidateAsync(candidate, cancellationToken).ConfigureAwait(false) is { } path)
                return path;
        }

        return null;
    }

    private static async Task<string?> ValidateAsync(CatalogImage entry, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(DirectoryPath, entry.FileName);
            await using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > MaxImageBytes)
                return null;

            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    private static async Task<Dictionary<DeviceIdentity, CatalogImage>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        var catalog = new Dictionary<DeviceIdentity, CatalogImage>();
        try
        {
            using var reader = File.OpenText(Path.Combine(DirectoryPath, "index.json"));
            if (reader.BaseStream.Length is <= 0 or > MaxCatalogBytes)
                return catalog;

            using var document = JsonDocument.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (GetProperty(root, "schemaVersion") is not { ValueKind: JsonValueKind.Number } version
                || !version.TryGetInt32(out var schema) || schema != 1
                || GetProperty(root, "entries") is not { ValueKind: JsonValueKind.Array } entries)
                return catalog;

            foreach (var item in entries.EnumerateArray())
            {
                if (ReadImage(item) is { } image && !catalog.TryAdd(image.Variant.Identity, image))
                    return new();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(error);
        }

        return catalog;
    }

    private static CatalogImage? ReadImage(JsonElement entry)
    {
        if (GetString(entry, "modelId") is not { } modelId || GetString(entry, "subModelId") is not { } subModelId
            || GetProperty(entry, "images") is not { } images || GetProperty(images, "hero") is not { } hero)
            return null;

        var identity = new DeviceIdentity(modelId, subModelId);
        var fileName = $"{modelId}_{subModelId}_hero.png";
        var sha256 = GetString(hero, "sha256");
        if (!IsExactIdentity(identity) || GetString(hero, "file") != fileName || !IsHex(sha256, 64))
            return null;

        var description = GetString(entry, "description") ?? subModelId;
        var isDefault = GetProperty(entry, "isCatalogDefault") is { ValueKind: JsonValueKind.True };
        return new CatalogImage(new OfficialArtworkVariant(identity, description, isDefault), fileName, sha256!);
    }

    private static JsonElement? GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        GetProperty(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool IsExactIdentity(DeviceIdentity identity) =>
        IsCatalogModelId(identity.ModelId) && IsHex(identity.SubModelId, 2, upperCaseOnly: true);

    private static bool IsCatalogModelId(string? modelId) =>
        modelId != "00000A" && IsHex(modelId, 6, upperCaseOnly: true);

    private static bool IsHex(string? value, int length, bool upperCaseOnly = false) =>
        value?.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'
            || !upperCaseOnly && character is >= 'a' and <= 'f');

    private sealed record CatalogImage(OfficialArtworkVariant Variant, string FileName, string Sha256);
}
