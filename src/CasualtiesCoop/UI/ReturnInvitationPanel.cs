using System.Collections.Generic;
using CasualtiesCoop.Lobby;
using CasualtiesCoop.Networking;
using CasualtiesCoop.Session;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CasualtiesCoop.UI;

internal sealed class ReturnInvitationPanel : MonoBehaviour
{
    private const float Width = 270;
    private const float RowHeight = 43;
    private readonly List<(CSteamID Player, RectTransform Row, Button Button, TextMeshProUGUI Label)> rows = new();
    private SteamLobby lobby = null!;
    private CSteamID lastAttempt;
    private string? result;
    private float nextRefresh;

    internal static ReturnInvitationPanel Attach(PauseHandler pause, SteamLobby lobby, RoomLaunchPlan plan, TMP_FontAsset font)
    {
        GameObject root = new("Co-op return invitations", typeof(RectTransform));
        RectTransform rect = (RectTransform)root.transform;
        rect.SetParent(pause.pauseContainer.transform, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
        rect.anchoredPosition = new Vector2(-24, -24);
        rect.sizeDelta = new Vector2(Width, (plan.Players.Count - 1) * RowHeight);

        ReturnInvitationPanel panel = root.AddComponent<ReturnInvitationPanel>();
        panel.lobby = lobby;
        foreach (LaunchPlayer player in plan.Players)
        {
            CSteamID id = new(player.SteamId);
            if (id != lobby.LocalUser)
            {
                panel.AddRow(id, rect, font);
            }
        }
        panel.Refresh();
        return panel;
    }

    private void AddRow(CSteamID player, RectTransform parent, TMP_FontAsset font)
    {
        GameObject row = new("Resend invitation", typeof(RectTransform));
        RectTransform rect = (RectTransform)row.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.up;
        rect.sizeDelta = new Vector2(Width, RowHeight - 6);
        Image background = row.AddComponent<Image>();
        Button button = row.AddComponent<Button>();
        button.targetGraphic = background;
        button.colors = CoopMenuView.ButtonColors;

        GameObject caption = new("Invitation caption", typeof(RectTransform));
        RectTransform captionRect = (RectTransform)caption.transform;
        captionRect.SetParent(rect, false);
        captionRect.anchorMax = Vector2.one;
        captionRect.anchorMin = Vector2.zero;
        captionRect.offsetMin = new Vector2(8, 0);
        captionRect.offsetMax = new Vector2(-8, 0);
        TextMeshProUGUI label = caption.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = 16;
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.richText = false;
        label.raycastTarget = false;
        button.onClick.AddListener(() =>
        {
            lastAttempt = player;
            result = lobby.ResendInvitation(player) ? "Invited" : "Could not invite";
            Refresh();
        });
        rows.Add((player, rect, button, label));
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextRefresh)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        nextRefresh = Time.unscaledTime + 0.25f;
        int visible = 0;
        foreach (var row in rows)
        {
            bool disconnected = lobby.IsHost && (!lobby.HasMember(row.Player) || lobby.Link.GetState(row.Player) != RoomLinkState.Verified);
            row.Row.gameObject.SetActive(disconnected);
            if (disconnected)
            {
                row.Row.anchoredPosition = new Vector2(0, -visible++ * RowHeight);
                row.Button.interactable = lobby.CanResendInvitation(row.Player);
                string action = row.Player == lastAttempt && !row.Button.interactable ? result ?? "Invite again" : "Invite again";
                row.Label.text = $"{action} · {SteamFriends.GetFriendPersonaName(row.Player)}";
            }
        }
    }
}
