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
        Assert.Contains("Text=\"HANDS-FREE\"", xaml);
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
        Assert.Contains("Content = \"Remove\"", codeBehind);
        Assert.Contains("OnRemoveDeviceClick", codeBehind);
        Assert.Contains("await _peerHost.ForgetAsync", codeBehind);
        Assert.DoesNotContain("public async void ShowPairingFailed", codeBehind);
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
    }
}
