using Xunit;

namespace Intercom.App.Tests.Shell;

public sealed class ApprovedPrototypeShellTests
{
    static readonly string XamlPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Intercom.App", "MainWindow.xaml"));
    static readonly string CodeBehindPath = Path.ChangeExtension(XamlPath, ".xaml.cs");

    [Fact]
    public void GroupFloorPanelUsesReplicatedServiceAndKeepsHandsFreeStateSeparate()
    {
        var code = File.ReadAllText(CodeBehindPath);

        Assert.Contains("new GroupFloorService", code);
        Assert.Contains("End Session for Everyone", code);
        Assert.Contains("ParticipantDeparted", code);
        Assert.Contains("GroupFloorQueuePanel.Children.Add", code);
        Assert.DoesNotContain("_hasGroupFloor = _handRaised", code);
        Assert.DoesNotContain("_handsFreeActive = _hasGroupFloor", code);
    }

    [Fact]
    public void MainWindow_ContainsTheApprovedIntegratedPrototypeRegions()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("Text=\"FAMILY\"", xaml);
        Assert.Contains("Text=\"GROUP FLOOR\"", xaml);
        Assert.Contains("x:Name=\"VoiceModeSwitch\"", xaml);
        Assert.Contains("x:Name=\"PushToTalkButton\"", xaml);
        Assert.Contains("Text=\"CHAT\"", xaml);
        Assert.Contains("Text=\"ATTENTION CARDS\"", xaml);
        Assert.Contains("Text=\"SEND AN ATTENTION CARD\"", xaml);
    }

    [Fact]
    public void MainWindow_DoesNotUseTheRejectedPlaceholderShell()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.DoesNotContain("Text=\"NEARBY DEVICES\"", xaml);
        Assert.DoesNotContain("Text=\"STATUS\"", xaml);
        Assert.DoesNotContain("demo peer", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachingIdentityStore_ImmediatelyRendersPersistedApprovedPeers()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains(
            "_identityStore = identityStore;\n        UpdateDiscoveredPeers(_lastDiscoveredPeers);",
            codeBehind.Replace("\r\n", "\n"));
    }

    [Fact]
    public void FamilyRoster_IsTheSharedRecipientSelector()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("x:Name=\"SelectedFamilyText\"", xaml);
        Assert.Contains("x:Name=\"ChatRecipientCombo\" Visibility=\"Collapsed\"", xaml);
        Assert.Contains("x:Name=\"HandsFreeRecipientCombo\" Visibility=\"Collapsed\"", xaml);
        Assert.Contains("x:Name=\"ComposerRecipientCombo\" Visibility=\"Collapsed\"", xaml);
        Assert.Contains("void OnFamilyMemberClick", codeBehind);
        Assert.Contains("Group selected:", codeBehind);
    }

    [Fact]
    public void PairingFailuresUseInlineNoticeAndFamilyRosterCanRemoveDevices()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("x:Name=\"PairingFailureNotice\"", xaml);
        Assert.Contains("Content = new SymbolIcon(Symbol.Delete)", codeBehind);
        Assert.Contains("OnRemoveDeviceClick", codeBehind);
        Assert.Contains("await _peerHost.ForgetAsync", codeBehind);
        Assert.DoesNotContain("public async void ShowPairingFailed", codeBehind);
    }

    [Fact]
    public void BackgroundReconnectFailure_DoesNotShowAUserAlert()
    {
        var appPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "App.xaml.cs"));
        var app = File.ReadAllText(appPath);

        Assert.Contains("_userInitiatedPeerConnections.TryRemove(peerId, out _)", app);
        Assert.Contains("if (showToUser)", app);
    }

    [Fact]
    public void PushToTalk_ObservesButtonHandledPointerPresses()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.DoesNotContain("PointerPressed=\"OnPushToTalkPressed\"", xaml);
        Assert.Contains("UIElement.PointerPressedEvent", codeBehind);
        Assert.Contains("handledEventsToo: true", codeBehind);
    }

    [Fact]
    public void AudioNegotiator_CanUseInboundConnectionAddressWithoutDiscovery()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("endpoint?.Address ?? _peerHost.ConnectedPeerAddress(peer.PeerId)", codeBehind);
    }

    [Fact]
    public void AudioDevices_AreManagedByWindowsSettingsInsteadOfTheVoiceCard()
    {
        var xaml = File.ReadAllText(XamlPath);
        var settingsPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "SettingsDialog.xaml"));
        var settingsXaml = File.ReadAllText(settingsPath);

        Assert.Contains("x:Name=\"SettingsButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"AudioInputCombo\"", xaml);
        Assert.DoesNotContain("x:Name=\"AudioOutputCombo\"", xaml);
        Assert.DoesNotContain("x:Name=\"AudioDiagnosticsText\"", xaml);
        Assert.DoesNotContain("x:Name=\"AudioInputCombo\"", settingsXaml);
        Assert.DoesNotContain("x:Name=\"AudioOutputCombo\"", settingsXaml);
        Assert.Contains("ms-settings:sound-defaultinputproperties", File.ReadAllText(Path.ChangeExtension(settingsPath, ".xaml.cs")));
        Assert.Contains("ms-settings:sound-defaultoutputproperties", File.ReadAllText(Path.ChangeExtension(settingsPath, ".xaml.cs")));
        Assert.Contains("new AudioGraphDevice()", File.ReadAllText(CodeBehindPath));
        Assert.DoesNotContain("x:Name=\"TestSpeakerButton\"", xaml);
        Assert.Contains("x:Name=\"TestSpeakerButton\"", settingsXaml);
        Assert.Contains("Symbol=\"Volume\"", settingsXaml);
    }

    [Fact]
    public void Settings_ExposeExplicitlyDisabledTwoPcMeasurementProfiles()
    {
        var settingsPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "SettingsDialog.xaml"));
        var settings = File.ReadAllText(settingsPath);
        var code = File.ReadAllText(Path.ChangeExtension(settingsPath, ".xaml.cs"));

        Assert.Contains("x:Name=\"AudioMeasurementProfileCombo\"", settings);
        Assert.Contains("Disabled (normal audio)", settings);
        Assert.Contains("1% packet loss", settings);
        Assert.Contains("5% packet loss", settings);
        Assert.Contains("Six-frame burst loss", settings);
        Assert.Contains("AudioMeasurementSettings.Save", code);
    }

    [Fact]
    public void Settings_ExposeAutomatedAudioLifecycleSoakTest()
    {
        var settingsPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "SettingsDialog.xaml"));
        var settings = File.ReadAllText(settingsPath);
        var code = File.ReadAllText(Path.ChangeExtension(settingsPath, ".xaml.cs"));
        var soakTestPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "Audio", "AudioLifecycleSoakTest.cs");
        var soakTest = File.ReadAllText(soakTestPath);

        Assert.Contains("x:Name=\"AudioLifecycleTestButton\"", settings);
        Assert.Contains("Run 100-cycle audio test", settings);
        Assert.Contains("AudioLifecycleSoakTest.RunAsync", code);
        Assert.Contains("public const int CycleCount = 100", soakTest);
        Assert.Contains("new AudioGraphDevice()", soakTest);
        Assert.Contains("rebound.Client.Bind", soakTest);
        Assert.Contains("GC.GetTotalMemory", soakTest);
        Assert.Contains("GetGuiResources", soakTest);
        Assert.Contains("audio.lifecycle-soak-checkpoint", soakTest);
        Assert.Contains("diagnostics.InputDeviceName", soakTest);
        Assert.Contains("diagnostics.OutputDeviceName", soakTest);
    }

    [Fact]
    public void ChatComposer_IsAnchoredToTheBottomOfTheChatCard()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("x:Name=\"ChatLayoutGrid\"", xaml);
        Assert.Contains("x:Name=\"ChatComposerGrid\" Grid.Row=\"4\"", xaml);
        Assert.Contains("<RowDefinition Height=\"*\"/>", xaml);
    }

    [Fact]
    public void PlainChatText_DoesNotAssignANullFontFamily()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.DoesNotContain("FontFamily = text.Code ? new FontFamily(\"Consolas\") : null", codeBehind);
        Assert.Contains("if (text.Code) run.FontFamily = new FontFamily(\"Consolas\");", codeBehind);
    }

    [Fact]
    public void MainLayout_UsesResponsiveColumnsAndReflowBreakpoints()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("x:Name=\"MainRegionsGrid\"", xaml);
        Assert.Contains("x:Name=\"FamilyColumn\" Width=\"210\"", xaml);
        Assert.Contains("x:Name=\"VoiceColumn\" Width=\"480\"", xaml);
        Assert.Contains("x:Name=\"ChatColumn\" Width=\"*\"", xaml);
        Assert.Contains("x:Name=\"MediumLayout\"", xaml);
        Assert.Contains("x:Name=\"NarrowLayout\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"1100\"", xaml);
    }

    [Fact]
    public void HandsFree_IsEnabledForOneConnectedSelectedPeer()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.DoesNotContain("HandsFreeButton.IsEnabled = false;", codeBehind);
        Assert.Contains("HandsFreeButton.IsEnabled = _handsFreeActive ||", codeBehind);
        Assert.Contains("_peerHost.ConnectedPeerIds.Contains(handsFreePeer.PeerId)", codeBehind);
    }

    [Fact]
    public void VoiceModes_UseRealSessionLifecycleAndDndGates()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("AudioInteractionMode.PushToTalk", codeBehind);
        Assert.Contains("AudioInteractionMode.HandsFree", codeBehind);
        Assert.Contains("session.StartTransmitting();", codeBehind);
        Assert.Contains("await negotiator.StopAsync", codeBehind);
        Assert.Contains("DndPolicy.IsSuppressed", codeBehind);
        Assert.Contains("RemoteDndEnabled", codeBehind);
    }

    [Fact]
    public void SoleOnlineFamilyMember_IsAutomaticallySelected()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("onlineApprovedPeers.Count == 1", codeBehind);
        Assert.Contains("_selectedFamilyPeerIds.Add(onlineApprovedPeers[0].PeerId)", codeBehind);
    }

    [Fact]
    public void FamilyRoster_UsesPresenceDotsAndIconOnlyRemoveAction()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("new Microsoft.UI.Xaml.Shapes.Ellipse", codeBehind);
        Assert.Contains("Fill = online ? AvailableGreen : DndRed", codeBehind);
        Assert.Contains("new SymbolIcon(Symbol.Delete)", codeBehind);
        Assert.Contains("ToolTipService.SetToolTip(remove", codeBehind);
    }

    [Fact]
    public void FamilyRoster_DoesNotRebuildWhenItsVisibleStateIsUnchanged()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);
        var snapshotGuard = codeBehind.IndexOf("if (snapshot == _renderedFamilyRosterSnapshot)", StringComparison.Ordinal);
        var clear = codeBehind.IndexOf("DiscoveredPeersList.Children.Clear();", StringComparison.Ordinal);

        Assert.True(snapshotGuard >= 0, "The family roster needs a stable-state snapshot guard.");
        Assert.True(snapshotGuard < clear, "The snapshot guard must run before replacing visible family rows.");
    }

    [Fact]
    public void AttentionPresets_AreLargeDirectSendButtonsWithTooltips()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.DoesNotContain("ComposerPresetCombo", xaml);
        Assert.DoesNotContain("Text=\"Purpose\"", xaml);
        Assert.Contains("Width = 64", codeBehind);
        Assert.Contains("ToolTipService.SetToolTip(button, preset.Purpose)", codeBehind);
        Assert.Contains("SendAttentionCardAsync(preset.Purpose, preset.Icon)", codeBehind);
    }

    [Fact]
    public void VoiceCard_SwitchesBetweenHoldAndToggleModes()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("x:Name=\"VoiceModeSwitch\"", xaml);
        Assert.Contains("OffContent=\"Hold to talk\" OnContent=\"Toggle on/off\"", xaml);
        Assert.Contains("if (VoiceModeSwitch.IsOn)", codeBehind);
        Assert.Contains("_handsFreeActive ? \"Turn\\nOff\" : \"Turn\\nOn\"", codeBehind);
    }

    [Fact]
    public void GroupFloor_IsOnlyVisibleForMultiSelection()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("x:Name=\"GroupFloorPanel\"", xaml);
        Assert.Contains("var groupVisible = selected.Count > 1", codeBehind);
        Assert.Contains("GroupFloorPanel.Visibility = groupVisible", codeBehind);
    }

    [Fact]
    public void MainRegions_FillAvailableHeightAndAttentionCardsStayAtBottom()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("x:Name=\"MainRegionsGrid\" Grid.Row=\"1\"", xaml);
        Assert.Contains("x:Name=\"PrimaryRegionsRow\" Height=\"*\"", xaml);
        Assert.Contains("x:Name=\"AttentionRegionsGrid\" Grid.Row=\"2\"", xaml);
        Assert.Contains("VerticalAlignment=\"Bottom\"", xaml);
    }

    [Fact]
    public void MainWindow_EnforcesContentMinimumSize()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("const int MinimumWindowWidth = 1050", codeBehind);
        Assert.Contains("const int MinimumWindowHeight = 720", codeBehind);
        Assert.Contains("AppWin.Changed += OnAppWindowChanged", codeBehind);
        Assert.Contains("AppWin.Resize", codeBehind);
    }

    [Fact]
    public void AlertsOverlayTheCardWithoutAddingALayoutRow()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("Canvas.ZIndex=\"10\"", xaml);
        Assert.DoesNotContain("<StackPanel Grid.Row=\"0\" Spacing=\"6\"", xaml);
        Assert.DoesNotContain("<Grid Grid.Row=\"1\" VerticalAlignment=\"Stretch\">", xaml);
    }

    [Fact]
    public void VoiceHeaderAlignsWithOtherColumnHeaders()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("Text=\"VOICE\" Style=\"{StaticResource SectionHeaderStyle}\" VerticalAlignment=\"Top\"", xaml);
        Assert.Contains("VoicePanel.RowSpacing = groupVisible ? 14 : 0", codeBehind);
    }

    [Fact]
    public void VoiceAndVisibleGroupFloorSplitTheirColumnEvenly()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("x:Name=\"GroupFloorRow\" Height=\"0\"", xaml);
        Assert.Contains("<RowDefinition Height=\"*\"/>", xaml);
        Assert.Contains("new GridLength(1, GridUnitType.Star)", codeBehind);
    }

    [Fact]
    public void RemotePresenceChanges_RefreshTheFamilyRosterAndShowDnd()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);

        Assert.Contains("presenceReceiver.LeaseAccepted += OnRemotePresenceChanged", codeBehind);
        Assert.Contains("void OnRemotePresenceChanged", codeBehind);
        Assert.Contains("Do Not Disturb", codeBehind);
        Assert.Contains("RemotePresenceLabel", codeBehind);
    }

    [Fact]
    public void ChatCard_ExposesRichDirectChatComposerAndViewOnlyImageViewer()
    {
        var xaml = File.ReadAllText(XamlPath);
        var codeBehind = File.ReadAllText(CodeBehindPath);
        var viewerPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "Chat", "ChatImageViewerWindow.cs"));

        Assert.Contains("x:Name=\"ChatImageButton\"", xaml);
        Assert.Contains("x:Name=\"ChatEmojiButton\"", xaml);
        Assert.Contains("AcceptsReturn=\"True\"", xaml);
        Assert.Contains("Shift", codeBehind);
        Assert.Contains("BuildMarkdownBlock", codeBehind);
        Assert.Contains("OpenChatLinkAsync", codeBehind);
        Assert.Contains("Group chat is not supported yet", codeBehind);
        Assert.Contains("DisplayArea.GetFromWindowId(AppWin.Id", codeBehind);
        Assert.Contains("RectInt32 workArea", File.ReadAllText(viewerPath));
        Assert.DoesNotContain("Save", File.ReadAllText(viewerPath));
    }

    [Fact]
    public void RichChat_DoesNotPutConversationContentInWindowsNotifications()
    {
        var codeBehind = File.ReadAllText(CodeBehindPath);
        var incomingStart = codeBehind.IndexOf("void OnIncomingChatMessage", StringComparison.Ordinal);
        var incomingEnd = codeBehind.IndexOf("void ShowChatChime", incomingStart, StringComparison.Ordinal);
        var handler = codeBehind[incomingStart..incomingEnd];

        Assert.DoesNotContain("ShowChatAsync", handler);
        Assert.Contains("ChatMarkdown.Parse", handler);
    }

    [Fact]
    public void GlobalPushToTalk_RegistersConflictVisibleHoldReleaseListener()
    {
        var inputPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "Input", "GlobalPushToTalkHotkey.cs"));
        var input = File.ReadAllText(inputPath);
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("RegisterHotKey", input);
        Assert.Contains("GetAsyncKeyState", input);
        Assert.Contains("RegistrationError", input);
        Assert.Contains("x:Name=\"HotkeyNotice\"", xaml);
    }

    [Fact]
    public void AudioFrames_QueryPrivateMemoryBufferComInterface()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "Audio", "AudioGraphDevice.cs"));
        var code = File.ReadAllText(path);

        Assert.Contains("WinRT.MarshalInspectable<object>.FromManaged(reference)", code);
        Assert.Contains("WinRT.MarshalInspectable<object>.DisposeAbi(unknown)", code);
        Assert.DoesNotContain("Marshal.GetIUnknownForObject(reference)", code);
        Assert.Contains("buffer.Length", code);
        Assert.Contains("frameEncoding.Subtype = MediaEncodingSubtypes.Float", code);
        Assert.Contains("buffer.Length = checked((uint)(destination.Length * sizeof(float)))", code);
        Assert.Contains("FloatPcmConverter.ToPcm16", code);
        Assert.Contains("FloatPcmConverter.ToFloat", code);
        Assert.Contains("CreateFrameOutputNode(frameEncoding)", code);
        Assert.Contains("CreateFrameInputNode(frameEncoding)", code);
        Assert.DoesNotContain("CreateFrameOutputNode(_graph.EncodingProperties)", code);
        Assert.DoesNotContain("CreateFrameInputNode(_graph.EncodingProperties)", code);
        Assert.Contains("Marshal.QueryInterface(unknown, in iid, out access)", code);
        Assert.Contains("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D", code);
        Assert.Contains("delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>", code);
        Assert.DoesNotContain("reference.As<IMemoryBufferByteAccess>()", code);
        Assert.Contains("Interlocked.Exchange(ref _callbackFailed, 1)", code);
        Assert.DoesNotContain("_graph?.Stop();\n        DeviceFailed?.Invoke", code.Replace("\r\n", "\n"));
        Assert.Contains("audio.callback-failed", code);
        Assert.Contains("RemoveOutgoingConnection", code);
        Assert.Contains("_capture?.Stop();", code);
        Assert.Contains("_input?.Stop();", code);
        Assert.Contains("_render.Stop();", code);
        Assert.Contains("_render.DiscardQueuedFrames();", code);
    }

    [Fact]
    public void AudioGraph_AllowsCaptureAndPlaybackDevicesToBeAbsentIndependently()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(XamlPath)!, "Audio", "AudioGraphDevice.cs"));
        var code = File.ReadAllText(path);

        Assert.Contains("audio.capture-unavailable", code);
        Assert.Contains("audio.playback-unavailable", code);
        Assert.DoesNotContain("throw new InvalidOperationException($\"Microphone creation failed", code);
        Assert.DoesNotContain("throw new InvalidOperationException($\"Speaker creation failed", code);
        Assert.Contains("No microphone (receive only)", code);
        Assert.Contains("No speaker (transmit only)", code);
    }
}
