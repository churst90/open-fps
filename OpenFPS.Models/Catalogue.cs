namespace OpenFPS.Models;

/// <summary>
/// The catalogue as one instance (docs/ENGINE_PACKAGES.md, stage 2; docs/SOUND_LIBRARY_BOUNDARY.md, stage 5):
/// what has been loaded and authored, from where, and what has been built from it. Its halves are beside the
/// static classes that were the registries: the models' in ModelLibrary.cs, the machines' in Machines.cs, a
/// machine's body in ModelKindTable.cs. Those static classes are facades over <see cref="Shared"/> until their
/// readers are handed an instance (decision 7 of the library survey); a test or a lab can make its own.
/// </summary>
public sealed partial class Catalogue
{
    /// <summary>The catalogue the static facades (ModelLibrary, MachineRegistry, ModelKindTable) read: the
    /// one the server, the client and the lab load their folders into.</summary>
    public static Catalogue Shared { get; } = new();
}
