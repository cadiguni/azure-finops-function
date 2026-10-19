using Personal.FinOpsApi.AzureFunctions.Models;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class CostExportManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CostExportManifest Parse(string json, string sourceManifest)
    {
        CostExportManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CostExportManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("O manifesto está vazio.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Manifesto inválido '{sourceManifest}': JSON malformado.",
                ex);
        }

        ValidateRequired(manifest, sourceManifest);
        return manifest;
    }

    private static void ValidateRequired(CostExportManifest manifest, string sourceManifest)
    {
        if (manifest.RunInfo is null || string.IsNullOrWhiteSpace(manifest.RunInfo.RunId))
        {
            throw Missing(sourceManifest, "runInfo.runId");
        }

        if (manifest.RunInfo.SubmittedTime == default)
        {
            throw Missing(sourceManifest, "runInfo.submittedTime");
        }

        if (manifest.ExportConfig is null || string.IsNullOrWhiteSpace(manifest.ExportConfig.ExportName))
        {
            throw Missing(sourceManifest, "exportConfig.exportName");
        }

        if (manifest.DeliveryConfig is null ||
            string.IsNullOrWhiteSpace(manifest.DeliveryConfig.RootFolderPath))
        {
            throw Missing(sourceManifest, "deliveryConfig.rootFolderPath");
        }

        if (manifest.Blobs is null || manifest.Blobs.Count == 0)
        {
            throw Missing(sourceManifest, "blobs");
        }

        for (var index = 0; index < manifest.Blobs.Count; index++)
        {
            if (manifest.Blobs[index] is null ||
                string.IsNullOrWhiteSpace(manifest.Blobs[index].BlobName))
            {
                throw Missing(sourceManifest, $"blobs[{index}].blobName");
            }
        }
    }

    private static InvalidDataException Missing(string sourceManifest, string property)
    {
        return new InvalidDataException(
            $"Manifesto inválido '{sourceManifest}': propriedade obrigatória ausente '{property}'.");
    }
}
