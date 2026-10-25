using System.Windows.Media;
using SamaDash.Services;

namespace SamaDash;

public enum EntryKind { App, File, Folder, Url, Add }

public enum EntrySource { Result, Suggested, Recent, Shortcut }

/// <summary>Un elemento cliccabile: risultato, suggerimento o shortcut.</summary>
public class Entry
{
    public required string Name { get; init; }
    /// <summary>Cosa aprire: file, cartella, .lnk, URL o shell:AppsFolder\AUMID.</summary>
    public required string Path { get; init; }
    public string Detail { get; init; } = "";
    public EntryKind Kind { get; init; }
    public EntrySource Source { get; init; }
    /// <summary>Exe di destinazione, per contare gli avvii.</summary>
    public string? Exe { get; init; }
    public string? IconPath { get; init; }

    ImageSource? icon;
    bool iconLoaded;
    public ImageSource? Icon
    {
        get
        {
            if (!iconLoaded && Kind != EntryKind.Add)
            {
                icon = IconCache.Get(IconPath ?? Path);
                iconLoaded = true;
            }
            return icon;
        }
    }

    public bool IsAdd => Kind == EntryKind.Add;
}
