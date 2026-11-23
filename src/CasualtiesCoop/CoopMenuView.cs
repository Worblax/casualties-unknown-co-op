using System;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CasualtiesCoop;

public sealed class CoopMenuView : MonoBehaviour
{
    private const float PanelWidth = 820f;
    private const float PanelHeight = 565f;
    private static readonly (RoomOptions Option, string Label)[] RoomToggles =
    {
        (RoomOptions.PvP, "PvP"),
        (RoomOptions.Respawn, "Respawn on the next layer"),
        (RoomOptions.KeepItems, "Keep some items after death"),
        (RoomOptions.KeepCheckpoint, "Keep checkpoint after team wipe")
    };

    private readonly RawImage[] avatars = new RawImage[SteamLobby.Capacity];
    private readonly TextMeshProUGUI[] members = new TextMeshProUGUI[SteamLobby.Capacity];
    private PreRunScript? menu;
    private Canvas? canvas;
    private GameObject? root;
    private RectTransform? panel;
    private GameObject? backdrop;
    private Button? createButton;
    private Button? inviteButton;
    private Button? readyButton;
    private Button? leaveButton;
    private Button? difficultyButton;
    private Button? startButton;
    private readonly Toggle[] ruleToggles = new Toggle[RoomToggles.Length];
    private Button[] characters = Array.Empty<Button>();
    private TextMeshProUGUI? status;
    private TextMeshProUGUI? difficulty;
    private TextMeshProUGUI? readyLabel;
    private TextMeshProUGUI? startHint;
    private LobbyAvatars? images;
    private float nextRefresh;
    private CSteamID previousRoom;

    internal bool PanelOpen => panel && panel!.gameObject.activeInHierarchy;

    internal void Install(PreRunScript owner)
    {
        menu = owner;
        canvas = owner.mainCanvas;
        images = new LobbyAvatars();
        RectTransform group = CreateRect("CasualtiesCoop", canvas.transform);
        root = group.gameObject;
        Stretch(group);
        group.SetAsLastSibling();

        RectTransform launch = CreateRect("Co-op entry", group);
        launch.anchorMin = Vector2.one;
        launch.anchorMax = Vector2.one;
        launch.pivot = Vector2.one;
        launch.anchoredPosition = new Vector2(-24, -24);
        launch.sizeDelta = new Vector2(180, 50);
        AddButton(launch, "Co-op", TogglePanel, out _);

        RectTransform dimmer = CreateRect("Co-op modal backdrop", group);
        Stretch(dimmer);
        backdrop = dimmer.gameObject;
        dimmer.gameObject.AddComponent<Image>().color = new Color32(7, 12, 13, 190);

        panel = CreateRect("Co-op panel", group);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        Image background = panel.gameObject.AddComponent<Image>();
        background.color = new Color32(17, 27, 28, 252);

        AddLabel(panel, "Steam co-op", 28, 22, 440, 42, 32);
        AddAction("Close", 688, 24, 104, 40, TogglePanel, out _);
        status = AddLabel(panel, "", 28, 72, 764, 44, 19);
        AddLabel(panel, "Players", 28, 130, 342, 28, 23);
        AddLabel(panel, "Room settings", 436, 130, 356, 28, 23);
        for (int index = 0; index < members.Length; index++)
        {
            int y = 174 + index * 43;
            RectTransform avatar = CreateRect("Avatar", panel);
            Place(avatar, 28, y, 34, 34);
            avatars[index] = avatar.gameObject.AddComponent<RawImage>();
            avatars[index].raycastTarget = false;
            members[index] = AddLabel(panel, "", 75, y, 334, 36, 18);
        }

        difficulty = AddLabel(panel, "", 436, 174, 220, 38, 18);
        difficultyButton = AddAction("Change", 688, 174, 104, 38, NextDifficulty, out _);
        for (int index = 0; index < ruleToggles.Length; index++)
        {
            var rule = RoomToggles[index];
            ruleToggles[index] = AddRuleToggle(rule.Label, 436, 231 + index * 41, rule.Option);
        }

        AddLabel(panel, "Your character", 28, 351, 342, 30, 19);
        characters = new Button[SteamLobby.CharacterCount];
        for (int index = 0; index < characters.Length; index++)
        {
            int character = index;
            float width = 380f / characters.Length;
            characters[index] = AddAction($"Character {index + 1}", 28 + index * width, 390,
                width - 7, 50, () => CoopPlugin.Lobby?.SelectCharacter(character), out _);
        }

        startHint = AddLabel(panel, "", 28, 452, 764, 40, 16);
        createButton = AddAction("Create Steam room", 28, 500, 274, 44, () => CoopPlugin.Lobby?.Create(), out _);
        inviteButton = AddAction("Invite friends", 28, 500, 164, 44, () => CoopPlugin.Lobby?.Invite(), out _);
        readyButton = AddAction("Ready", 207, 500, 155, 44, () => CoopPlugin.Lobby?.ToggleReady(), out var readyText);
        readyLabel = readyText;
        leaveButton = AddAction("Leave room", 377, 500, 154, 44, () => CoopPlugin.Lobby?.Leave(), out _);
        startButton = AddAction("Start", 602, 500, 190, 44, () => CoopPlugin.Lobby?.StartSoloPreview(menu!), out _);
        SetPanelOpen(false);
    }

    private void Update()
    {
        if (!root || !menu)
        {
            return;
        }

        CoopPlugin.TickMenu();
        bool visible = !menu!.contentWarning.activeInHierarchy && !menu.tutorialWarning.activeInHierarchy &&
            !ScrollableText.textExists && !menu.creditsScreen.activeInHierarchy &&
            !menu.runSettingsScreen.activeInHierarchy && !SettingsMenu.instance;
        root!.SetActive(visible);
        if (!visible || Time.unscaledTime < nextRefresh)
        {
            return;
        }

        nextRefresh = Time.unscaledTime + 0.25f;
        SteamLobby? lobby = CoopPlugin.Lobby;
        if (lobby is { InRoom: true } && lobby.Current != previousRoom)
        {
            previousRoom = lobby.Current;
            SetPanelOpen(true);
        }
        else if (lobby is not { InRoom: true })
        {
            previousRoom = CSteamID.Nil;
        }

        if (panel!.gameObject.activeSelf)
        {
            Refresh(lobby);
        }
    }

    private void TogglePanel()
    {
        SetPanelOpen(!panel!.gameObject.activeSelf);
        if (panel.gameObject.activeSelf)
        {
            Refresh(CoopPlugin.Lobby);
        }
    }

    private void SetPanelOpen(bool open)
    {
        backdrop!.SetActive(open);
        panel!.gameObject.SetActive(open);
        if (open && canvas!.transform is RectTransform bounds)
        {
            float scale = Mathf.Min(1f, (bounds.rect.width - 48) / PanelWidth,
                (bounds.rect.height - 48) / PanelHeight);
            panel.localScale = Vector3.one * Mathf.Max(0.25f, scale);
        }
    }

    private void Refresh(SteamLobby? lobby)
    {
        bool inRoom = lobby is { InRoom: true };
        bool isHost = inRoom && lobby!.IsHost;
        int count = lobby?.MemberCount ?? 0;
        status!.text = lobby?.Status ?? (inRoom ? $"Players {count}/{SteamLobby.Capacity}" :
            lobby is null ? CoopPlugin.SteamStatus : "Start a room and invite your team.");

        createButton!.gameObject.SetActive(!inRoom);
        createButton.interactable = lobby is { IsBusy: false };
        inviteButton!.gameObject.SetActive(isHost);
        readyButton!.gameObject.SetActive(inRoom);
        leaveButton!.gameObject.SetActive(inRoom);
        startButton!.gameObject.SetActive(isHost);
        startButton.interactable = isHost && lobby!.CanStartSolo;
        difficulty!.gameObject.SetActive(inRoom);
        difficultyButton!.gameObject.SetActive(isHost);
        startHint!.text = inRoom
            ? count > 1 ? "Launching a shared run is still in development."
                : "Start opens a solo preview. Room options are preview controls. Saving is disabled."
            : "";
        RoomOptions options = lobby?.Options ?? RoomOptions.Defaults;
        for (int index = 0; index < ruleToggles.Length; index++)
        {
            Toggle toggle = ruleToggles[index];
            toggle.gameObject.SetActive(inRoom);
            toggle.interactable = isHost;
            toggle.SetIsOnWithoutNotify((options & RoomToggles[index].Option) != 0);
        }

        for (int index = 0; index < members.Length; index++)
        {
            bool present = index < count;
            members[index].gameObject.SetActive(present);
            avatars[index].gameObject.SetActive(present);
            if (!present)
            {
                continue;
            }

            CSteamID player = SteamMatchmaking.GetLobbyMemberByIndex(lobby!.Current, index);
            string host = player == SteamMatchmaking.GetLobbyOwner(lobby.Current) ? " (host)" : "";
            string ready = lobby.TryGetReadyCharacter(player, out int character)
                ? $"Ready · {CharacterName(character)}"
                : "Waiting";
            members[index].text = $"{SteamFriends.GetFriendPersonaName(player)}{host}   {ready}";
            avatars[index].texture = images!.Get(player);
            avatars[index].color = avatars[index].texture ? Color.white : new Color32(52, 70, 68, 255);
        }

        for (int index = 0; index < characters.Length; index++)
        {
            Button button = characters[index];
            button.gameObject.SetActive(inRoom);
            if (!inRoom)
            {
                continue;
            }

            bool unlocked = SteamLobby.IsCharacterUnlocked(index);
            bool selected = lobby!.SelectedCharacter == index;
            button.interactable = unlocked && !selected;
            button.GetComponentInChildren<TextMeshProUGUI>().text = CharacterName(index) +
                (!unlocked ? "\nLocked" : selected ? "\nSelected" : "");
        }

        if (inRoom)
        {
            difficulty.text = $"Difficulty: {lobby!.Difficulty}";
            readyLabel!.text = lobby.IsReady(lobby.LocalUser) ? "Cancel ready" : "Ready";
        }
    }

    private static string CharacterName(int character) => Locale.GetOther($"char{character}name");

    private static void NextDifficulty()
    {
        if (CoopPlugin.Lobby is not { IsHost: true } lobby)
        {
            return;
        }

        int index = RunSettings.presets.FindIndex(preset => preset.presetName == lobby.Difficulty);
        lobby.ChangeDifficulty(RunSettings.presets[(index + 1) % RunSettings.presets.Count].presetName);
    }

    private Button AddAction(string label, float x, float y, float width, float height,
        Action action, out TextMeshProUGUI text)
    {
        RectTransform rect = CreateRect(label, panel!);
        Place(rect, x, y, width, height);
        return AddButton(rect, label, action, out text);
    }

    private Button AddButton(RectTransform rect, string label, Action action, out TextMeshProUGUI text)
    {
        Image image = rect.gameObject.AddComponent<Image>();
        image.color = new Color32(48, 83, 76, 255);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => action());
        text = AddLabel(rect, label, 0, 0, rect.sizeDelta.x, rect.sizeDelta.y, 17);
        text.alignment = TextAlignmentOptions.Center;
        return button;
    }

    private TextMeshProUGUI AddLabel(RectTransform parent, string value, float x, float y,
        float width, float height, float size)
    {
        RectTransform rect = CreateRect("Label", parent);
        Place(rect, x, y, width, height);
        TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = menu!.versionText.font;
        label.text = value;
        label.fontSize = size;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Max(12, size - 5);
        label.fontSizeMax = size;
        label.color = new Color32(222, 239, 231, 255);
        label.richText = false;
        label.raycastTarget = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return label;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject item = new(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)item.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private Toggle AddRuleToggle(string label, float x, float y, RoomOptions option)
    {
        RectTransform row = CreateRect(label, panel!);
        Place(row, x, y, 352, 34);
        Image background = row.gameObject.AddComponent<Image>();
        background.color = new Color32(34, 53, 51, 255);
        Toggle toggle = row.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = background;
        RectTransform check = CreateRect("Check", row);
        Place(check, 9, 9, 16, 16);
        Image indicator = check.gameObject.AddComponent<Image>();
        indicator.color = new Color32(143, 216, 182, 255);
        indicator.raycastTarget = false;
        toggle.graphic = indicator;
        AddLabel(row, label, 35, 2, 308, 29, 17).alignment = TextAlignmentOptions.MidlineLeft;
        toggle.onValueChanged.AddListener(value => CoopPlugin.Lobby?.ChangeOption(option, value));
        return toggle;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = Vector2.up;
        rect.anchorMax = Vector2.up;
        rect.pivot = Vector2.up;
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private void OnDestroy()
    {
        images?.Dispose();
        if (root)
        {
            Destroy(root);
        }
    }
}
