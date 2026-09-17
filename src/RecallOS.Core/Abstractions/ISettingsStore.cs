using RecallOS.Core.Models;

namespace RecallOS.Core.Abstractions;

/// <summary>Reads and writes the one <see cref="RecallSettings"/> instance the app shares.</summary>
public interface ISettingsStore
{
    /// <summary>The live settings object. Mutations are not persisted until saved.</summary>
    RecallSettings Current { get; }

    Task<RecallSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RecallSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Raised after a successful save so services can re-read what changed.</summary>
    event EventHandler<RecallSettings>? SettingsChanged;
}
