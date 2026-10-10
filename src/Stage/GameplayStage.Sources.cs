using System.Runtime;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// The stage, library, characters, and instruments of a song: the stage choice and the package cache folders.
internal sealed partial class GameplayStage
{
    // Stages that can load: the installed venue packages.
    private static List<string> AvailableStages(Packages.PackageManager manager)
    {
        var ids = manager.Packages.Where(p => p.Manifest.Kind == Packages.PackageKind.Stage && !p.SongPackage && p.State != Packages.PackageState.Failed)
            .Select(p => p.Manifest.Id).ToList();
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    // The player's choice (in-game picker, GH3_SKIN_STAGE for tests), the song's suggestion, and the installed stages
    // select the stage once for each song (docs/PLAYER-GUIDE.md).
    private void ChooseStage(Packages.PackageManager manager, Packages.InstalledPackage? library)
    {
        var choices = Packages.PackageRuntime.Choices;
        var entry = GlobalVariables.instance?.currentSongEntry;
        var index = library?.State == Packages.PackageState.Ready ? Path.Combine(library.CacheDirectory, "songs", "index.json") : "";
        Packages.StageSuggestion? suggestion = null;
        try
        {
            if (entry != null) suggestion = Packages.StageSelection.Suggestion(entry.folderPath, entry.Name_StrippedTags, entry.Artist_StrippedTags, index);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MelonLogger.Warning($"GH3 stage: the song folder could not be read for a stage suggestion ({error.Message})");
        }
        var decision = Packages.StageSelection.Choose(choices, SkinEnvironment.Get(SkinEnvironment.Stage), AvailableStages(manager), suggestion,
            StageRandom, _previousRandom);
        if (decision.PackagePath.Length > 0)
        {
            _songPackage = manager.AddSongPackage(decision.PackagePath);
            if (_songPackage == null)
            {
                decision = Packages.StageSelection.Choose(choices, SkinEnvironment.Get(SkinEnvironment.Stage), AvailableStages(manager), null, StageRandom, _previousRandom);
                decision = decision with { Reason = decision.Reason + " (the song's venue package did not load)" };
            }
            else decision = decision with { StageId = _songPackage.Manifest.Id };
        }
        if (decision.Reason.StartsWith("random", StringComparison.Ordinal)) _previousRandom = decision.StageId;
        _decision = decision;
        _stageId = decision.StageId.Length == 0 || StagePack.ValidId(decision.StageId) ? decision.StageId : "";
        MelonLogger.Msg($"GH3 stage choice: {(_stageId.Length > 0 ? _stageId : "none")} ({decision.Reason}; player choice {choices.Stage}, " +
            $"song suggestions {(choices.SongSuggestions ? "on" : "off")})");
        StageChartReader.UseVenueTrack = Packages.PlayerChoices.Switch(SkinEnvironment.Get(SkinEnvironment.VenueTrack), choices.VenueTrack);
        StageChartReader.UseGeneratedDirection = Packages.PlayerChoices.Switch(SkinEnvironment.Get(SkinEnvironment.GeneratedDirection), choices.GeneratedDirection);
        StageRockBandVenue.ReduceFlashing = SkinEnvironment.Get(SkinEnvironment.ReduceFlashing) is { } reduce ?
            reduce is not ("off" or "0") : choices.ReduceFlashing;
        MelonLogger.Msg($"GH3 direction: Rock Band VENUE tracks {(StageChartReader.UseVenueTrack ? "on" : "off")}, " +
            $"generated direction {(StageChartReader.UseGeneratedDirection ? "on" : "off")}, reduce flashing {(StageRockBandVenue.ReduceFlashing ? "on" : "off")}");
        _characterPackages.Clear();
        foreach (var role in Packages.PlayerChoices.Roles)
        {
            var id = SkinEnvironment.Get(SkinEnvironment.CharacterPrefix + role.ToUpperInvariant()) ?? choices.Character(role);
            if (id == Packages.PlayerChoices.DefaultCharacter || id.Length == 0) continue;
            var character = manager.Find(Packages.PackageKind.Character, id);
            if (character == null || character.State == Packages.PackageState.Failed)
                MelonLogger.Warning($"GH3 band {role}: character {id} is not installed or did not import; the venue default plays");
            else _characterPackages[role] = character;
        }
        _instrumentPackages.Clear();
        foreach (var role in Packages.PlayerChoices.InstrumentRoles)
        {
            var id = SkinEnvironment.Get(SkinEnvironment.InstrumentPrefix + role.ToUpperInvariant()) ?? choices.Instrument(role);
            if (id == Packages.PlayerChoices.DefaultInstrument || id.Length == 0) continue;
            var instrument = manager.Find(Packages.PackageKind.Instrument, id);
            if (instrument == null || instrument.State == Packages.PackageState.Failed)
                MelonLogger.Warning($"GH3 band {role}: instrument {id} is not installed or did not import; the default instrument plays");
            else _instrumentPackages[role] = instrument;
        }
    }

    // The stage and the library come from their package cache folders. The shader bundles come from the mod installation
    // (Mods/GH3Skin/render), because they depend on the graphics API and the Unity version.
    private SourceState ResolveSources()
    {
        var manager = Packages.PackageRuntime.Manager;
        if (_waitingFor.Length == 0) LoadTiming.Mark("stage resolves its packages");
        if (manager == null || !manager.Scanned) return Wait("the package scan");
        static bool Busy(Packages.InstalledPackage? package) => package?.State is Packages.PackageState.Pending or Packages.PackageState.Importing;
        if (_decision == null)
        {
            // The library has the song index for the stage suggestion of GH3 career songs.
            var songIndex = manager.Find(Packages.PackageKind.Library, "gh3-band-v1");
            if (Busy(songIndex))
            {
                manager.Prioritize(songIndex!);
                return Wait("the import of gh3-band-v1");
            }
            ChooseStage(manager, songIndex);
        }
        if (_stageId.Length == 0) return SourceState.Missing;
        var stage = _songPackage ?? manager.Find(Packages.PackageKind.Stage, _stageId);
        if (stage == null) return SourceState.Missing;
        var requirement = stage.Manifest.Requires.FirstOrDefault(r => r.Id.StartsWith("gh3-band-", StringComparison.Ordinal));
        var library = manager.Find(Packages.PackageKind.Library, requirement.Id ?? "gh3-band-v1");
        if (Busy(stage) || Busy(library) || _characterPackages.Values.Any(Busy) || _instrumentPackages.Values.Any(Busy))
        {
            manager.Prioritize(stage);
            foreach (var character in _characterPackages.Values.Concat(_instrumentPackages.Values)) manager.Prioritize(character);
            return Wait($"the import of {string.Join(", ", new[] { stage, library }.Concat(_characterPackages.Values).Concat(_instrumentPackages.Values).Where(Busy).Select(p => p!.Manifest.Id))}");
        }
        if (stage.State != Packages.PackageState.Ready || library?.State != Packages.PackageState.Ready)
        {
            MelonLogger.Warning($"GH3 stage package {_stageId} did not import ({stage.Error}{(library == null ? "library gh3-band-v1 is not installed" : library.Error)})");
            return SourceState.Failed;
        }
        _directory = stage.CacheDirectory;
        _library = library.CacheDirectory;
        _events = Path.Combine(library.CacheDirectory, "songs");
        _effectsDirectory = Path.Combine(library.CacheDirectory, "render");
        _render = Path.GetFullPath(Path.Combine(_assetsDirectory, "..", "render"));
        MelonLogger.Msg($"GH3 stage source: package {stage.Label}, library {library.Label}");
        return SourceState.Ready;
    }

    private Dictionary<string, Packages.CharacterModel> LoadCharacters()
    {
        var result = new Dictionary<string, Packages.CharacterModel>();
        foreach (var (role, package) in _characterPackages)
        {
            if (package.State != Packages.PackageState.Ready)
            {
                MelonLogger.Warning($"GH3 band {role}: character {package.Manifest.Id} is not ready ({package.State} {package.Error}); the venue default plays");
                continue;
            }
            try
            {
                var model = Packages.CharacterModel.Load(package.CacheDirectory);
                if (model.Library.Length > 0)
                {
                    // A character library profile: the character's import needed the library, so it is ready here.
                    var library = Packages.PackageRuntime.Manager?.Find(Packages.PackageKind.Library, model.Library);
                    if (library?.State != Packages.PackageState.Ready) throw new Packages.PackageException($"its library {model.Library} is not ready");
                    model.LibraryFolder = library.CacheDirectory;
                }
                result[role] = model;
            }
            catch (Exception error) when (error is IOException or Packages.PackageException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                MelonLogger.Warning($"GH3 band {role}: character {package.Manifest.Id} could not load ({error.Message}); the venue default plays");
            }
        }
        return result;
    }

    // The player's instruments: a guitar for the guitarist and a bass for the bassist.
    private Dictionary<string, Packages.InstrumentModel> LoadInstruments()
    {
        var result = new Dictionary<string, Packages.InstrumentModel>();
        foreach (var (role, package) in _instrumentPackages)
        {
            if (package.State != Packages.PackageState.Ready)
            {
                MelonLogger.Warning($"GH3 band {role}: instrument {package.Manifest.Id} is not ready ({package.State} {package.Error}); the default instrument plays");
                continue;
            }
            try
            {
                var model = Packages.InstrumentModel.Load(package.CacheDirectory);
                if (model.Role != role)
                    MelonLogger.Warning($"GH3 band {role}: instrument {model.Id} is a {model.Kind} for the {model.Role}; the default instrument plays");
                else result[role] = model;
            }
            catch (Exception error) when (error is IOException or Packages.PackageException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                MelonLogger.Warning($"GH3 band {role}: instrument {package.Manifest.Id} could not load ({error.Message}); the default instrument plays");
            }
        }
        return result;
    }

    private SourceState Wait(string what)
    {
        if (_waitingFor != what) MelonLogger.Msg($"GH3 stage waits for {what}");
        _waitingFor = what;
        return SourceState.Waiting;
    }
}
