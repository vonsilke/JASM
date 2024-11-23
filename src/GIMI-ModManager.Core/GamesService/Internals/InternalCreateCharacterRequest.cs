﻿using GIMI_ModManager.Core.GamesService.Models;

namespace GIMI_ModManager.Core.GamesService.Internals;

internal class InternalCreateCharacterRequest
{
    public required InternalName InternalName { get; set; }

    public string? DisplayName { get; set; }

    public DateTime? ReleaseDate { get; set; }

    public string? Image { get; set; }

    public int Rarity { get; set; }

    public string? Element { get; set; }

    public string? Class { get; set; }

    public string[]? Region { get; set; }

    public string? ModFilesName { get; set; }

    public bool IsMultiMod { get; set; }

    public string[]? Keys { get; set; }
}