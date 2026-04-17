using Azure.Data.Tables;
using Azure.Storage.Blobs;
using CFItems.DataPipeline.Models;
using System.Text.Json;

namespace CFItems.DataPipeline.Services;

public class AzureStorageService
{
    private readonly string _sasToken;
    private readonly string _tableEndpoint;
    private readonly string _blobEndpoint;

    public AzureStorageService(string sasToken, string tableEndpoint, string blobEndpoint)
    {
        _sasToken = sasToken;
        _tableEndpoint = tableEndpoint;
        _blobEndpoint = blobEndpoint;
    }

    public async Task<List<ItemRecord>> ExportAllItemsAsync(string outputPath)
    {
        Console.WriteLine("Exporting all items from Azure Table Storage...");
        var uri = new Uri($"{_tableEndpoint}cfitems?{_sasToken}");
        var tableClient = new TableClient(uri);

        var items = new List<ItemRecord>();
        await foreach (var item in tableClient.QueryAsync<ItemRecord>())
        {
            items.Add(item);
        }

        Console.WriteLine($"Exported {items.Count} items");

        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json);
        Console.WriteLine($"Saved to {outputPath}");

        return items;
    }

    public async Task<List<string>> ListLogFilesAsync()
    {
        var uri = new Uri($"{_blobEndpoint}orginal?{_sasToken}");
        var containerClient = new BlobContainerClient(uri);

        var blobs = new List<string>();
        await foreach (var blob in containerClient.GetBlobsAsync())
        {
            blobs.Add(blob.Name);
        }

        Console.WriteLine($"Found {blobs.Count} log files in blob storage");
        return blobs;
    }

    public async Task DownloadLogFilesAsync(string outputDir)
    {
        var uri = new Uri($"{_blobEndpoint}orginal?{_sasToken}");
        var containerClient = new BlobContainerClient(uri);

        Directory.CreateDirectory(outputDir);

        var count = 0;
        await foreach (var blob in containerClient.GetBlobsAsync())
        {
            var localPath = Path.Combine(outputDir, blob.Name);
            if (File.Exists(localPath))
            {
                count++;
                continue;
            }

            var blobClient = containerClient.GetBlobClient(blob.Name);
            await blobClient.DownloadToAsync(localPath);
            count++;
            if (count % 50 == 0)
                Console.WriteLine($"Downloaded {count} log files...");
        }

        Console.WriteLine($"Downloaded {count} log files to {outputDir}");
    }

    public async Task UpdateItemAreaAsync(string itemName, string area)
    {
        var uri = new Uri($"{_tableEndpoint}cfitems?{_sasToken}");
        var tableClient = new TableClient(uri);

        try
        {
            var response = await tableClient.GetEntityAsync<ItemRecord>("CFItems", itemName);
            var item = response.Value;
            item.Area = area;
            await tableClient.UpsertEntityAsync(item);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update area for '{itemName}': {ex.Message}");
        }
    }

    public async Task BatchUpdateAreasAsync(Dictionary<string, string> itemAreas)
    {
        var updates = itemAreas.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value, (string?)null, (string?)null, (string?)null));
        await BatchUpdateAllFieldsAsync(updates);
    }

    public async Task BatchUpdateAllFieldsAsync(
        Dictionary<string, (string? Area, string? Mob, string? Container, string? Path)> items)
    {
        var uri = new Uri($"{_tableEndpoint}cfitems?{_sasToken}");
        var tableClient = new TableClient(uri);

        var count = 0;
        var failed = 0;
        foreach (var (itemName, fields) in items)
        {
            try
            {
                var response = await tableClient.GetEntityAsync<ItemRecord>("CFItems", itemName);
                var item = response.Value;
                var changed = false;

                if (!string.IsNullOrEmpty(fields.Area) && item.Area != fields.Area)
                { item.Area = fields.Area; changed = true; }
                if (!string.IsNullOrEmpty(fields.Mob) && item.MobSource != fields.Mob)
                { item.MobSource = fields.Mob; changed = true; }
                if (!string.IsNullOrEmpty(fields.Container) && item.ContainerSource != fields.Container)
                { item.ContainerSource = fields.Container; changed = true; }
                if (!string.IsNullOrEmpty(fields.Path) && item.PathFromCrossroads != fields.Path)
                { item.PathFromCrossroads = fields.Path; changed = true; }

                if (changed)
                {
                    await tableClient.UpsertEntityAsync(item);
                    count++;
                    if (count % 50 == 0)
                        Console.WriteLine($"Updated {count} items...");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 5)
                    Console.WriteLine($"Failed to update '{itemName}': {ex.Message}");
                else if (failed == 6)
                    Console.WriteLine("(suppressing further error messages)");
            }
        }

        Console.WriteLine($"Updated {count} items in Azure Table ({failed} failures)");
    }

    /// <summary>
    /// Complete reset-and-update for location data: iterate all items in the table,
    /// and for any item NOT in the provided dictionary, CLEAR Area/MobSource/ContainerSource/PathFromCrossroads.
    /// For items IN the dictionary, set those fields. This removes stale bad attributions.
    /// </summary>
    public async Task ResetAndUpdateAllLocationsAsync(
        Dictionary<string, (string? Area, string? Mob, string? Container, string? Path)> items)
    {
        var uri = new Uri($"{_tableEndpoint}cfitems?{_sasToken}");
        var tableClient = new TableClient(uri);

        Console.WriteLine("Scanning all items in Azure Table...");
        var allItems = new List<ItemRecord>();
        await foreach (var item in tableClient.QueryAsync<ItemRecord>())
            allItems.Add(item);
        Console.WriteLine($"Found {allItems.Count} items total.");

        var updated = 0;
        var cleared = 0;
        var failed = 0;

        foreach (var item in allItems)
        {
            if (string.IsNullOrEmpty(item.RowKey)) continue;

            try
            {
                var changed = false;
                var key = item.RowKey;

                if (items.TryGetValue(key, out var fields))
                {
                    // Set new values (write even if null, to clear stale ones not in this update)
                    if (item.Area != fields.Area)
                    { item.Area = fields.Area; changed = true; }
                    if (item.MobSource != fields.Mob)
                    { item.MobSource = fields.Mob; changed = true; }
                    if (item.ContainerSource != fields.Container)
                    { item.ContainerSource = fields.Container; changed = true; }
                    if (item.PathFromCrossroads != fields.Path)
                    { item.PathFromCrossroads = fields.Path; changed = true; }
                    if (changed) updated++;
                }
                else
                {
                    // Not in new data - clear all location fields if any are set
                    if (!string.IsNullOrEmpty(item.Area) || !string.IsNullOrEmpty(item.MobSource) ||
                        !string.IsNullOrEmpty(item.ContainerSource) || !string.IsNullOrEmpty(item.PathFromCrossroads))
                    {
                        item.Area = null;
                        item.MobSource = null;
                        item.ContainerSource = null;
                        item.PathFromCrossroads = null;
                        changed = true;
                        cleared++;
                    }
                }

                if (changed)
                {
                    await tableClient.UpsertEntityAsync(item);
                    var total = updated + cleared;
                    if (total % 50 == 0)
                        Console.WriteLine($"Progress: {updated} updated, {cleared} cleared ({failed} failures)");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 5)
                    Console.WriteLine($"Failed on '{item.RowKey}': {ex.Message}");
                else if (failed == 6)
                    Console.WriteLine("(suppressing further error messages)");
            }
        }

        Console.WriteLine($"Done. Updated {updated} items with fresh data, cleared {cleared} stale items ({failed} failures)");
    }
}
