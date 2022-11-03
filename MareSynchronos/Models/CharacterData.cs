﻿using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MareSynchronos.API;
using MareSynchronos.Utils;
using Lumina.Excel.GeneratedSheets;

namespace MareSynchronos.Models;

[JsonObject(MemberSerialization.OptIn)]
public class CharacterData
{
    [JsonProperty]
    public Dictionary<ObjectKind, List<FileReplacement>> FileReplacements { get; set; } = new();

    [JsonProperty]
    public Dictionary<ObjectKind, string> GlamourerString { get; set; } = new();

    public bool IsReady => FileReplacements.SelectMany(k => k.Value).All(f => f.Computed);

    [JsonProperty]
    public string ManipulationString { get; set; } = string.Empty;

    [JsonProperty]
    public float HeelsOffset { get; set; } = 0f;

    [JsonProperty]
    public string CustomizePlusScale { get; set; } = string.Empty;

    public void AddFileReplacement(ObjectKind objectKind, FileReplacement fileReplacement)
    {
        if (!fileReplacement.HasFileReplacement) return;

        if (!FileReplacements.ContainsKey(objectKind)) FileReplacements.Add(objectKind, new List<FileReplacement>());

        var existingReplacement = FileReplacements[objectKind].SingleOrDefault(f => string.Equals(f.ResolvedPath, fileReplacement.ResolvedPath, System.StringComparison.OrdinalIgnoreCase));
        if (existingReplacement != null)
        {
            existingReplacement.GamePaths.AddRange(fileReplacement.GamePaths.Where(e => !existingReplacement.GamePaths.Contains(e, System.StringComparer.OrdinalIgnoreCase)));
        }
        else
        {
            FileReplacements[objectKind].Add(fileReplacement);
        }
    }

    public CharacterCacheDto ToCharacterCacheDto()
    {
        var fileReplacements = FileReplacements.ToDictionary(k => k.Key, k => k.Value.Where(f => f.HasFileReplacement && !f.IsFileSwap).GroupBy(f => f.Hash, System.StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            return new FileReplacementDto()
            {
                GamePaths = g.SelectMany(f => f.GamePaths).Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray(),
                Hash = g.First().Hash,
            };
        }).ToList());

        Logger.Debug("Adding fileSwaps");
        foreach (var item in FileReplacements)
        {
            Logger.Debug("Checking fileSwaps for " + item.Key);
            var fileSwapsToAdd = item.Value.Where(f => f.IsFileSwap).Select(f => f.ToFileReplacementDto());
            Logger.Debug("Adding " + fileSwapsToAdd.Count() + " file swaps");
            foreach (var swap in fileSwapsToAdd)
            {
                Logger.Debug("Adding: " + swap.GamePaths.First() + ":" + swap.FileSwapPath);
            }
            fileReplacements[item.Key].AddRange(fileSwapsToAdd);
        }

        return new CharacterCacheDto()
        {
            FileReplacements = fileReplacements,
            GlamourerData = GlamourerString.ToDictionary(d => d.Key, d => d.Value),
            ManipulationData = ManipulationString,
            HeelsOffset = HeelsOffset,
            CustomizePlusData = CustomizePlusScale
        };
    }

    public override string ToString()
    {
        StringBuilder stringBuilder = new();
        foreach (var fileReplacement in FileReplacements.SelectMany(k => k.Value).OrderBy(a => a.GamePaths[0]))
        {
            stringBuilder.AppendLine(fileReplacement.ToString());
        }
        return stringBuilder.ToString();
    }
}
