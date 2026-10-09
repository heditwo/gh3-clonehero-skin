using Gh3Skin.Packages;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Gh3Skin;

// The in-game stage and band picker: a small IMGUI window of our own that opens with F6 in Clone Hero's menus (not in
// gameplay). It changes no Clone Hero object; while it is open, Clone Hero's EventSystem is off so that a mouse click on
// the window does not also click the menu below it. The choices go to UserData/GH3Skin/choices.json (PlayerChoices).
internal sealed class PickerOverlay
{
    public const KeyCode Key = KeyCode.F6;
    private const float DesignHeight = 720;
    private sealed record StageRow(string Id, string Name, string Detail, string Preview);
    private sealed record CharacterRow(string Id, string Name, string Role);
    private readonly Dictionary<string, Texture2D?> _previews = new();
    private List<StageRow> _stages = new();
    private List<CharacterRow> _characters = new();
    private Dictionary<string, string[]> _rigs = new();
    private EventSystem? _blockedEvents;
    private Vector2 _scroll;
    private string _hover = "";
    private bool _keyDownSeen;
    public bool Open { get; private set; }
    public bool Menus { get; set; }

    public void Toggle()
    {
        if (Open) Close();
        else Show();
    }

    public void Show()
    {
        if (!Menus || Open) return;
        Refresh();
        Open = true;
        try
        {
            var events = EventSystem.current;
            if (events != null && events.enabled)
            {
                events.enabled = false;
                _blockedEvents = events;
            }
        }
        catch (Exception error) { MelonLogger.Warning($"GH3 picker: Clone Hero's menu input could not pause ({error.Message})"); }
        MelonLogger.Msg($"GH3 picker opened: {_stages.Count} stages, {_characters.Count} characters");
    }

    public void Close()
    {
        if (!Open) return;
        Open = false;
        if (_blockedEvents != null) _blockedEvents.enabled = true;
        _blockedEvents = null;
        PackageRuntime.SaveChoices();
        var choices = PackageRuntime.Choices;
        MelonLogger.Msg($"GH3 picker closed: stage {choices.Stage}, song suggestions {(choices.SongSuggestions ? "on" : "off")}, " +
            string.Join(", ", PlayerChoices.Roles.Select(role => $"{role} {choices.Character(role)}")));
    }

    // Scene changes destroy nothing of ours, but Clone Hero makes a new EventSystem; give the old one back first.
    public void SceneChanged(bool menus)
    {
        if (Open) Close();
        Menus = menus;
    }

    public void Update()
    {
        if (!Menus) return;
        try
        {
            if (Input.GetKeyDown(Key)) Toggle();
            _keyDownSeen = true;
        }
        catch (Exception) { _keyDownSeen = false; }
    }

    private void Refresh()
    {
        var manager = PackageRuntime.Manager;
        var packages = manager?.Packages ?? new List<InstalledPackage>();
        _stages = packages.Where(p => p.Manifest.Kind == PackageKind.Stage && !p.SongPackage && p.State != PackageState.Failed)
            .Select(p => new StageRow(p.Manifest.Id, p.Manifest.Name, p.State == PackageState.Ready ? p.Manifest.Id : p.Manifest.Id + " (importing)",
                p.CacheDirectory.Length > 0 ? Path.Combine(p.CacheDirectory, "preview.png") : ""))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var library = manager?.Find(PackageKind.Library, "gh3-band-v1");
        _rigs = new Dictionary<string, string[]>();
        if (library?.State == PackageState.Ready)
            foreach (var role in PlayerChoices.Roles)
                try { _rigs[role] = CharacterImporter.Rig(library.CacheDirectory, role).Bones; }
                catch (Exception error) when (error is IOException or PackageException or System.Text.Json.JsonException) { }
        _characters = new List<CharacterRow>();
        _incompatible.Clear();
        foreach (var package in packages.Where(p => p.Manifest.Kind == PackageKind.Character && p.State == PackageState.Ready))
            try
            {
                var model = CharacterModel.Load(package.CacheDirectory);
                _characters.Add(new CharacterRow(package.Manifest.Id, package.Manifest.Name, model.Role));
                foreach (var role in PlayerChoices.Roles.Where(r => r != model.Role && (!_rigs.TryGetValue(r, out var rig) || model.Map(rig) == null)))
                    _incompatible.Add((package.Manifest.Id, role));
            }
            catch (Exception error) when (error is IOException or PackageException or System.Text.Json.JsonException) { }
    }

    private readonly HashSet<(string Id, string Role)> _incompatible = new();

    // A character can play its own role, and another role whose rig has all of its joints (the GH3 guitarist and bassist).
    private List<CharacterRow> Choices(string role) =>
        _characters.Where(c => c.Role == role || !_incompatible.Contains((c.Id, role))).OrderBy(c => c.Role == role ? 0 : 1).ThenBy(c => c.Name).ToList();

    public void OnGUI()
    {
        if (!Menus || _failed) return;
        try { Draw(); }
        catch (Exception error)
        {
            // IMGUI methods that Clone Hero's build strips fail here ("Method unstripping failed"); stop instead of failing each frame.
            _failed = true;
            if (Open) Close();
            MelonLogger.Warning($"GH3 picker is off: {error.Message}");
        }
    }

    private bool _failed;

    private void Draw()
    {
        var current = Event.current;
        if (!_keyDownSeen && current.type == EventType.KeyDown && current.keyCode == Key)
        {
            Toggle();
            current.Use();
        }
        var scale = Screen.height / DesignHeight;
        var width = Screen.width / scale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
        if (!Open)
        {
            if (PackageRuntime.Choices.ShowHint)
                GUI.Label(new Rect(width / 2 - 200, 4, 400, 20), $"<color=#ffffffb0>GH3 stage: {StageLabel(PackageRuntime.Choices.Stage)}   (F6: stage and band)</color>");
            return;
        }
        var panel = new Rect(width / 2 - 470, 40, 940, 640);
        GUI.Box(panel, GUIContent.none, Style(Background()));
        GUI.Label(new Rect(panel.x + 16, panel.y + 10, 600, 24), "<b><size=18>GH3 Skin: stage and band</size></b>");
        DrawStages(new Rect(panel.x + 16, panel.y + 44, 420, 560));
        DrawDetails(new Rect(panel.x + 452, panel.y + 44, 472, 560));
        if (GUI.Button(new Rect(panel.xMax - 136, panel.yMax - 36, 120, 28), "Close (F6)")) Close();
        GUI.Label(new Rect(panel.x + 16, panel.yMax - 32, 640, 24), "Click to select. The choice applies to the next song.");
    }

    private string StageLabel(string id) => id switch
    {
        PlayerChoices.RandomStage => "Random",
        PlayerChoices.SongStage => "Song's choice",
        _ => _stages.FirstOrDefault(s => s.Id == id)?.Name ?? PackageRuntime.Manager?.Find(PackageKind.Stage, id)?.Manifest.Name ?? id,
    };

    private void DrawStages(Rect area)
    {
        var choices = PackageRuntime.Choices;
        GUI.Label(new Rect(area.x, area.y, area.width, 22), "<b>Stage</b>");
        var rows = new List<(string Id, string Label)> { (PlayerChoices.SongStage, "Song's choice (else random)"), (PlayerChoices.RandomStage, "Random") };
        rows.AddRange(_stages.Select(s => (s.Id, $"{s.Name}  <color=#999999>{s.Detail}</color>")));
        var view = new Rect(0, 0, area.width - 20, rows.Count * 30);
        _scroll = GUI.BeginScrollView(new Rect(area.x, area.y + 26, area.width, area.height - 26), _scroll, view);
        for (var i = 0; i < rows.Count; i++)
        {
            var (id, label) = rows[i];
            var rect = new Rect(0, i * 30, view.width, 28);
            if (rect.Contains(Event.current.mousePosition)) _hover = id;
            var selected = choices.Stage == id;
            if (GUI.Toggle(rect, selected, (selected ? "> " : "  ") + label, GUI.skin.button) && !selected)
            {
                choices.Stage = id;
                PackageRuntime.SaveChoices();
            }
        }
        GUI.EndScrollView();
    }

    private void DrawDetails(Rect area)
    {
        var choices = PackageRuntime.Choices;
        var shown = _stages.FirstOrDefault(s => s.Id == _hover) ?? _stages.FirstOrDefault(s => s.Id == choices.Stage);
        var image = new Rect(area.x, area.y, 448, 252);
        GUI.Box(image, "");
        // GUI.DrawTexture is stripped in this build; a style background draws the image stretched to the 16:9 box.
        if (shown != null && Preview(shown) is { } texture) GUI.Box(image, GUIContent.none, Style(texture));
        else GUI.Label(new Rect(image.x + 16, image.y + 116, 420, 22), shown == null ? StageLabel(_hover.Length > 0 ? _hover : choices.Stage) : "No preview image");
        GUI.Label(new Rect(area.x, image.yMax + 4, 448, 22), shown?.Name ?? StageLabel(choices.Stage));
        var suggestions = GUI.Toggle(new Rect(area.x, image.yMax + 30, 448, 22), choices.SongSuggestions, " Songs can select their stage");
        if (suggestions != choices.SongSuggestions)
        {
            choices.SongSuggestions = suggestions;
            PackageRuntime.SaveChoices();
        }
        var hint = GUI.Toggle(new Rect(area.x, image.yMax + 54, 448, 22), choices.ShowHint, " Show the F6 hint in the menus");
        if (hint != choices.ShowHint)
        {
            choices.ShowHint = hint;
            PackageRuntime.SaveChoices();
        }
        GUI.Label(new Rect(area.x, image.yMax + 86, 448, 22), "<b>Band</b>" + (_characters.Count == 0 ? "  <color=#999999>(no character packages in Custom/Characters)</color>" : ""));
        for (var i = 0; i < PlayerChoices.Roles.Length; i++)
        {
            var role = PlayerChoices.Roles[i];
            var y = image.yMax + 112 + i * 32;
            var options = Choices(role);
            var ids = new List<string> { PlayerChoices.DefaultCharacter };
            ids.AddRange(options.Select(c => c.Id));
            var current = choices.Character(role);
            var index = ids.IndexOf(current);
            GUI.Label(new Rect(area.x, y + 4, 90, 22), char.ToUpperInvariant(role[0]) + role[1..]);
            var step = GUI.Button(new Rect(area.x + 92, y, 30, 26), "<") ? -1 : GUI.Button(new Rect(area.x + 418, y, 30, 26), ">") ? 1 : 0;
            var row = options.FirstOrDefault(c => c.Id == current);
            var label = current == PlayerChoices.DefaultCharacter ? "Default (venue)" :
                row == null ? current + " (not installed)" : row.Role == role ? row.Name : $"{row.Name} <color=#999999>(a {row.Role})</color>";
            GUI.Box(new Rect(area.x + 126, y, 288, 26), label);
            if (step != 0)
            {
                choices.Characters[role] = ids[((index < 0 ? 0 : index) + step + ids.Count) % ids.Count];
                PackageRuntime.SaveChoices();
            }
        }
    }

    private Texture2D? _background;
    private readonly Dictionary<IntPtr, GUIStyle> _styles = new();

    private GUIStyle Style(Texture2D texture)
    {
        if (_styles.TryGetValue(texture.Pointer, out var style)) return style;
        style = new GUIStyle();
        style.normal.background = texture;
        _styles[texture.Pointer] = style;
        return style;
    }

    private Texture2D Background()
    {
        if (_background != null) return _background;
        _background = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        _background.SetPixels(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color>(new[] { new Color(0.04f, 0.05f, 0.08f, 0.94f) }));
        _background.Apply();
        return _background;
    }

    private Texture2D? Preview(StageRow stage)
    {
        if (_previews.TryGetValue(stage.Id, out var cached)) return cached;
        Texture2D? texture = null;
        if (stage.Preview.Length > 0 && File.Exists(stage.Preview))
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(stage.Preview), true)) texture = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { texture = null; }
        _previews[stage.Id] = texture;
        return texture;
    }

    // Test aid (GH3_SKIN_PICKER_TEST, scripts/capture-menu.sh): the same selection code as the buttons, without a mouse.
    public void Apply(string stage, IReadOnlyDictionary<string, string> characters)
    {
        Refresh();
        var choices = PackageRuntime.Choices;
        if (stage.Length > 0 && PlayerChoices.ValidStage(stage)) choices.Stage = stage;
        foreach (var (role, id) in characters)
            if (PlayerChoices.Roles.Contains(role) && (id == PlayerChoices.DefaultCharacter || Choices(role).Any(c => c.Id == id))) choices.Characters[role] = id;
            else MelonLogger.Warning($"GH3 picker test: {id} cannot play the {role}");
        _hover = choices.Stage;
        PackageRuntime.SaveChoices();
    }
}
