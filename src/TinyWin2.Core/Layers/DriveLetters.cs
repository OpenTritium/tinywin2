namespace TinyWin2.Core.Layers;

/// <summary>
///     Drive-letter selection for VHD mount points. Only S..Z are used: low letters
///     collide with ISO mounts and other transient reservations that
///     Directory.GetLogicalDrives() cannot see.
/// </summary>
internal static class DriveLetters {
    /// <exception cref="IOException">no letter in the reserved S..Z range is free.</exception>
    public static char FirstFreeMountLetter(string purpose) {
        var used = Directory.GetLogicalDrives()
            .Select(d => char.ToUpperInvariant(d[0]))
            .ToHashSet();
        for (var letter = 'S'; letter <= 'Z'; letter++) {
            if (!used.Contains(letter)) {
                return letter;
            }
        }

        throw new IOException($"no free drive letter in S..Z for {purpose}");
    }
}
