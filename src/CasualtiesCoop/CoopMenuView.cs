using System;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CasualtiesCoop;

public sealed class CoopMenuView : MonoBehaviour
{
    private const float PanelWidth = 410f;
    private const float PanelHeight = 492f;
    private readonly RawImage[] avatars = new RawImage[SteamLobby.Capacity];
    private readonly TextMeshProUGUI[] members = new TextMeshProUGUI[SteamLobby.Capacity];
    private PreRunScript? menu;
    private Canvas? canvas;
    private GameObject? root;
    private RectTransform? panel;
    private Button? createButton;
    private Button? inviteButton;
    private Button? readyButton;
    private Button? leaveButton;
    private Button? difficultyButton;
    private Button[] characters = Array.Empty<Button>();
    private TextMeshProUGUI? status;
    private TextMeshProUGUI? difficulty;
    private TextMeshProUGUI? readyLabel;
    private LobbyAvatars? images;
    private float nextRefresh;
    private CSteamID previousRoom;

    internal void Install(PreRunScript owner)
    {
        menu = owner;
        canvas = owner.mainCanvas;
        images = new LobbyAvatars();
        RectTransform group = CreateRect("CasualtiesCoop", canvas.transform);
        root = group.gameObject;
        group.anchorMin = Vector2.one;
        group.anchorMax = Vector2.one;
        group.pivot = Vector2.one;
        group.anchoredPosition = new Vector2(-24, -24);
        group.sizeDelta = new Vector2(PanelWidth, PanelHeight + 56);
        group.SetAsLastSibling();

        RectTransform launch = CreateRect("Co-op entry", group);
        Place(launch, PanelWidth - 156, 0, 156, 44);
        AddButton(launch, "Co-op", TogglePanel, out _);

        panel = CreateRect("Co-op panel", group);
        Place(panel, 0, 56, PanelWidth, PanelHeight);
        Image background = panel.gameObject.AddComponent<Image>();
        background.color = new Color32(17, 27, 28, 252);

        AddLabel(panel, "Steam co-op", 20, 12, 280, 28, 24);
        AddAction("Close", 320, 12, 70, 30, TogglePanel, out _);
        status = AddLabel(panel, "", 20, 46, 370, 50, 16);
        for (int index = 0; index < members.Length; index++)
        {
            int y = 105 + index * 38;
            RectTransform avatar = CreateRect("Avatar", panel);
            Place(avatar, 20, y, 30, 30);
            avatars[index] = avatar.gameObject.AddComponent<RawImage>();
            avatars[index].raycastTarget = false;
            members[index] = AddLabel(panel, "", 62, y, 330, 30, 17);
        }

        difficulty = AddLabel(panel, "", 20, 266, 225, 36, 17);
        difficultyButton = AddAction("Change", 268, 266, 122, 36, NextDifficulty, out _);
        characters = new Button[SteamLobby.CharacterCount];
        for (int index = 0; index < characters.Length; index++)
        {
            int character = index;
            float width = 362f / characters.Length;
            characters[index] = AddAction($"Character {index + 1}", 20 + index * width, 320,
                width - 5, 44, () => CoopPlugin.Lobby?.SelectCharacter(character), out _);
        }

        createButton = AddAction("Create Steam room", 20, 400, 370, 42, () => CoopPlugin.Lobby?.Create(), out _);
        inviteButton = AddAction("Invite friends", 20, 400, 119, 42, () => CoopPlugin.Lobby?.Invite(), out _);
        readyButton = AddAction("Ready", 146, 400, 119, 42, () => CoopPlugin.Lobby?.ToggleReady(), out var readyText);
        readyLabel = readyText;
        leaveButton = AddAction("Leave room", 272, 400, 118, 42, () => CoopPlugin.Lobby?.Leave(), out _);
        panel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!root || !menu)
        {
            return;
        }

        CoopPlugin.TickMenu();
        bool visible = !menu!.contentWarning.activeInHierarchy && !ScrollableText.textExists &&
            !menu.creditsScreen.activeInHierarchy && !menu.runSettingsScreen.activeInHierarchy;
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
            panel!.gameObject.SetActive(true);
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
        panel!.gameObject.SetActive(!panel.gameObject.activeSelf);
        if (panel.gameObject.activeSelf)
        {
            Refresh(CoopPlugin.Lobby);
        }
    }

    private void Refresh(SteamLobby? lobby)
    {
        bool inRoom = lobby is { InRoom: true };
        bool isHost = inRoom && lobby!.IsHost;
        int count = inRoom ? SteamMatchmaking.GetNumLobbyMembers(lobby!.Current) : 0;
        status!.text = lobby?.Status ?? (inRoom ? $"Players {count}/{SteamLobby.Capacity}" :
            lobby is null ? CoopPlugin.SteamStatus : "Start a room and invite your team.");

        createButton!.gameObject.SetActive(!inRoom);
        createButton.interactable = lobby is { IsBusy: false };
        inviteButton!.gameObject.SetActive(isHost);
        readyButton!.gameObject.SetActive(inRoom);
        leaveButton!.gameObject.SetActive(inRoom);
        difficulty!.gameObject.SetActive(inRoom);
        difficultyButton!.gameObject.SetActive(isHost);

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
