using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using CheapLoc;

using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Interface.Animation.EasingFunctions;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.ManagedFontAtlas.Internals;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.AutoUpdate;
using Dalamud.Plugin.Services;
using Dalamud.Storage.Assets;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;

using ImGuiNET;
using ImGuiScene;
using Serilog;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>
/// For major updates, an in-game Changelog window.
/// </summary>
internal sealed class ChangelogWindow : Window, IDisposable
{
    private const string WarrantsChangelogForMajorMinor = "10.0.";
    
    private const string ChangeLog =
        @"• 更新了 Dalamud 以兼容版本 7.0
• 进行了大量幕后更改，使 Dalamud 和插件更加稳定可靠
• 添加了开发人员可以利用的新功能
• 重新设计了 Dalamud/插件安装程序 UI
• 将Roaming文件夹重新放回了AppData中
";

    private static readonly TimeSpan TitleScreenWaitTime = TimeSpan.FromSeconds(0.5f); 

    private readonly TitleScreenMenuWindow tsmWindow;

    private readonly GameGui gameGui;

    private readonly DisposeSafety.ScopedFinalizer scopedFinalizer = new();
    private readonly IFontAtlas privateAtlas;
    private readonly Lazy<IFontHandle> bannerFont;
    private readonly Lazy<IDalamudTextureWrap> apiBumpExplainerTexture;
    private readonly Lazy<IDalamudTextureWrap> logoTexture;
    private readonly Lazy<IDalamudTextureWrap> fontTipsTexture;

    private readonly InOutCubic windowFade = new(TimeSpan.FromSeconds(2.5f))
    {
        Point1 = Vector2.Zero,
        Point2 = new Vector2(2f),
    };
    
    private readonly InOutCubic bodyFade = new(TimeSpan.FromSeconds(0.8f))
    {
        Point1 = Vector2.Zero,
        Point2 = Vector2.One,
    };
    
    private readonly InOutCubic titleFade = new(TimeSpan.FromSeconds(0.5f))
    {
        Point1 = Vector2.Zero,
        Point2 = Vector2.One,
    };
    
    private readonly InOutCubic fadeOut = new(TimeSpan.FromSeconds(0.5f))
    {
        Point1 = Vector2.One,
        Point2 = Vector2.Zero,
    };
    
    private State state = State.WindowFadeIn;
    
    private bool needFadeRestart = false;
    
    private bool isFadingOutForStateChange = false;
    private State? stateAfterFadeOut;
    
    private AutoUpdateBehavior? chosenAutoUpdateBehavior;
    
    private Dictionary<string, int> currentFtueLevels = new();

    private DateTime? isEligibleSince;
    private bool openedThroughEligibility;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangelogWindow"/> class.
    /// </summary>
    /// <param name="tsmWindow">TSM window.</param>
    /// <param name="fontAtlasFactory">An instance of <see cref="FontAtlasFactory"/>.</param>
    /// <param name="assets">An instance of <see cref="DalamudAssetManager"/>.</param>
    /// <param name="gameGui">An instance of <see cref="GameGui"/>.</param>
    /// <param name="framework">An instance of <see cref="Framework"/>.</param>
    public ChangelogWindow(
        TitleScreenMenuWindow tsmWindow,
        FontAtlasFactory fontAtlasFactory,
        DalamudAssetManager assets,
        GameGui gameGui,
        Framework framework)
        : base("What's new in Dalamud?##ChangelogWindow", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, true)
    {
        this.gameGui = gameGui;
        
        this.tsmWindow = tsmWindow;
        this.Namespace = "DalamudChangelogWindow";
        this.privateAtlas = this.scopedFinalizer.Add(
            fontAtlasFactory.CreateFontAtlas(this.Namespace, FontAtlasAutoRebuildMode.Async));
        this.bannerFont = new(
            () => this.scopedFinalizer.Add(
                this.privateAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.MiedingerMid18))));

        this.apiBumpExplainerTexture = new(() => assets.GetDalamudTextureWrap(DalamudAsset.ChangelogApiBumpIcon));
        this.logoTexture = new(() => assets.GetDalamudTextureWrap(DalamudAsset.Logo));

        this.fontTipsTexture = new(() => assets.GetDalamudTextureWrap(DalamudAsset.MissingFontTips));

        // If we are going to show a changelog, make sure we have the font ready, otherwise it will hitch
        if (WarrantsChangelog())
            _ = this.bannerFont.Value;
        
        framework.Update += this.FrameworkOnUpdate;
        this.scopedFinalizer.Add(() => framework.Update -= this.FrameworkOnUpdate);
    }

    private enum State
    {
        WindowFadeIn,
        ExplainerIntro,
        ExplainerApiBump,
        AskAutoUpdate,
        Links,
    }
    
    /// <summary>
    /// Check if a changelog should be shown.
    /// </summary>
    /// <returns>True if a changelog should be shown.</returns>
    public static bool WarrantsChangelog()
    {
        var configuration = Service<DalamudConfiguration>.Get();
        var pm = Service<PluginManager>.GetNullable();
        var pmWantsChangelog = pm?.InstalledPlugins.Any() ?? true;
        return (string.IsNullOrEmpty(configuration.LastChangelogMajorMinor) ||
                (!WarrantsChangelogForMajorMinor.StartsWith(configuration.LastChangelogMajorMinor) &&
                 Util.AssemblyVersion.StartsWith(WarrantsChangelogForMajorMinor))) && pmWantsChangelog;
    }

    /// <inheritdoc/>
    public override void OnOpen()
    {
        Service<DalamudInterface>.Get().SetCreditsDarkeningAnimation(true);
        this.tsmWindow.AllowDrawing = false;

        _ = this.bannerFont;

        this.isFadingOutForStateChange = false;
        this.stateAfterFadeOut = null;
        
        this.state = State.WindowFadeIn;
        this.windowFade.Reset();
        this.bodyFade.Reset();
        this.titleFade.Reset();
        this.fadeOut.Reset();
        this.needFadeRestart = true;

        this.chosenAutoUpdateBehavior = null;
        
        this.currentFtueLevels = Service<DalamudConfiguration>.Get().SeenFtueLevels;
        
        base.OnOpen();
    }

    /// <inheritdoc/>
    public override void OnClose()
    {
        base.OnClose();
        
        this.tsmWindow.AllowDrawing = true;
        Service<DalamudInterface>.Get().SetCreditsDarkeningAnimation(false);

        var configuration = Service<DalamudConfiguration>.Get();
        
        if (this.chosenAutoUpdateBehavior.HasValue)
        {
            configuration.AutoUpdateBehavior = this.chosenAutoUpdateBehavior.Value;
        }
        
        configuration.SeenFtueLevels = this.currentFtueLevels;
        configuration.QueueSave();
    }

    /// <inheritdoc/>
    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        
        base.PreDraw();

        if (this.needFadeRestart)
        {
            this.windowFade.Restart();
            this.titleFade.Restart();
            this.needFadeRestart = false;
        }
        
        this.windowFade.Update();
        this.titleFade.Update();
        this.fadeOut.Update();
        ImGui.SetNextWindowBgAlpha(Math.Clamp(this.windowFade.EasedPoint.X, 0, 0.9f));
        
        this.Size = new Vector2(900, 400);
        this.SizeCondition = ImGuiCond.Always;
        
        // Center the window on the main viewport
        var viewportPos = ImGuiHelpers.MainViewport.Pos;
        var viewportSize = ImGuiHelpers.MainViewport.Size;
        var windowSize = this.Size!.Value * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(new Vector2(viewportPos.X + viewportSize.X / 2 - windowSize.X / 2, viewportPos.Y + viewportSize.Y / 2 - windowSize.Y / 2));
        // ImGui.SetNextWindowPos(new Vector2(viewportSize.X / 2 - windowSize.X / 2, viewportSize.Y / 2 - windowSize.Y / 2));
    }

    /// <inheritdoc/>
    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        
        this.ResetMovieTimer();
        
        base.PostDraw();
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        void Dismiss()
        {
            var configuration = Service<DalamudConfiguration>.Get();
            configuration.LastChangelogMajorMinor = WarrantsChangelogForMajorMinor;
            configuration.QueueSave();
        }
        
        var windowSize = ImGui.GetWindowSize();
        
        var dummySize = 10 * ImGuiHelpers.GlobalScale;
        ImGui.Dummy(new Vector2(dummySize));
        ImGui.SameLine();
        
        var logoContainerSize = new Vector2(windowSize.X * 0.2f - dummySize, windowSize.Y);
        using (var child = ImRaii.Child("###logoContainer", logoContainerSize, false))
        {
            if (!child)
                return;

            var logoSize = new Vector2(logoContainerSize.X);
            
            // Center the logo in the container
            ImGui.SetCursorPos(new Vector2(logoContainerSize.X / 2 - logoSize.X / 2, logoContainerSize.Y / 2 - logoSize.Y / 2));
            
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Clamp(this.windowFade.EasedPoint.X - 0.5f, 0f, 1f)))
                ImGui.Image(this.logoTexture.Value.ImGuiHandle, logoSize);
        }
        
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(dummySize));
        ImGui.SameLine();
        
        using (var child = ImRaii.Child("###textContainer", new Vector2((windowSize.X * 0.8f) - dummySize * 4, windowSize.Y), false))
        {
            if (!child)
                return;
            
            ImGuiHelpers.ScaledDummy(20);

            var titleFadeVal = this.isFadingOutForStateChange ? this.fadeOut.EasedPoint.X : this.titleFade.EasedPoint.X;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Clamp(titleFadeVal, 0f, 1f)))
            {
                using var font = this.bannerFont.Value.Push();

                switch (this.state)
                {
                    case State.WindowFadeIn:
                    case State.ExplainerIntro:
                        ImGuiHelpers.CenteredText("New And Improved");
                        break;
                    
                    case State.ExplainerApiBump:
                        ImGuiHelpers.CenteredText("Plugin Updates");
                        break;
                    
                    case State.AskAutoUpdate:
                        ImGuiHelpers.CenteredText("Auto-Updates");
                        break;
                    
                    case State.Links:
                        ImGuiHelpers.CenteredText("Enjoy!");
                        break;
                }
            }
            
            ImGuiHelpers.ScaledDummy(8);

            if (this.state == State.WindowFadeIn && this.windowFade.EasedPoint.X > 1.5f)
            {
                this.state = State.ExplainerIntro;
                this.bodyFade.Restart();
            }

            if (this.isFadingOutForStateChange && this.fadeOut.IsDone)
            {
                this.state = this.stateAfterFadeOut ?? throw new Exception("State after fade out is null");

                this.bodyFade.Restart();
                this.titleFade.Restart();
                
                this.isFadingOutForStateChange = false;
                this.stateAfterFadeOut = null;
            }
            
            this.bodyFade.Update();
            var bodyFadeVal = this.isFadingOutForStateChange ? this.fadeOut.EasedPoint.X : this.bodyFade.EasedPoint.X;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Clamp(bodyFadeVal, 0, 1f)))
            {
                void GoToNextState(State nextState)
                {
                    this.isFadingOutForStateChange = true;
                    this.stateAfterFadeOut = nextState;
                        
                    this.fadeOut.Restart();
                }
                
                bool DrawNextButton(State nextState)
                {
                    // Draw big, centered next button at the bottom of the window
                    var buttonHeight = 30 * ImGuiHelpers.GlobalScale;
                    var buttonText = "Next";
                    var buttonWidth = ImGui.CalcTextSize(buttonText).X + 40 * ImGuiHelpers.GlobalScale;
                    ImGui.SetCursorPosY(windowSize.Y - buttonHeight - (20 * ImGuiHelpers.GlobalScale));
                    ImGuiHelpers.CenterCursorFor((int)buttonWidth);
                
                    if (ImGui.Button(buttonText, new Vector2(buttonWidth, buttonHeight)) && !this.isFadingOutForStateChange)
                    {
                        GoToNextState(nextState);
                        return true;
                    }

                    return false;
                }
                
                switch (this.state)
                {
                    case State.WindowFadeIn:
                    case State.ExplainerIntro:
                        ImGui.TextWrapped($"Welcome to Dalamud v{Util.GetScmVersion()}!");
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped(ChangeLog);
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped("这个更新日志是对本版本中最重要的更改的快速概述");
                        ImGui.TextWrapped("请点击 Next 以查看更新插件的快速指南。");

                        ImGui.Image(
                            this.fontTipsTexture.Value.ImGuiHandle,
                            this.fontTipsTexture.Value.Size);

                        var interfaceManager = Service<InterfaceManager>.Get();
                        using (interfaceManager.MonoFontHandle?.Push())
                        {
                            if (ImGui.Button(Loc.Localize("DalamudSettingResetDefaultFont", "Reset Default Font")))
                            {
                                var faf = Service<FontAtlasFactory>.Get();
                                faf.DefaultFontSpecOverride =
                                        new SingleFontSpec { FontId = new GameFontAndFamilyId(GameFontFamily.Axis) };
                                interfaceManager.RebuildFonts();

                                Service<DalamudConfiguration>.Get().DefaultFontSpec = faf.DefaultFontSpecOverride;
                                Service<DalamudConfiguration>.Get().QueueSave();
                            }
                        }

                        ImGuiHelpers.ScaledDummy(10);
                        DrawNextButton(State.ExplainerApiBump);
                        break;
                    
                    case State.ExplainerApiBump:
                        ImGui.TextWrapped("注意了！由于此补丁的更改，所有插件都需要更新，并已自动禁用");
                        ImGui.TextWrapped("这是正常情况，也是进行重大游戏更新所必需的。");
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped("要更新插件，请打开插件安装程序，然后点击“更新插件”。更新后的插件应会自动更新并重新启用。");
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped("请记住，并非你所有的插件都可能已经针对新版本进行了更新。");
                        ImGui.TextWrapped("如果在“已安装插件”选项卡中显示某些插件带有红色叉号，那么它们可能还不可用。");
                        
                        ImGuiHelpers.ScaledDummy(15);

                        ImGuiHelpers.CenterCursorFor(this.apiBumpExplainerTexture.Value.Width);
                        ImGui.Image(
                            this.apiBumpExplainerTexture.Value.ImGuiHandle,
                            this.apiBumpExplainerTexture.Value.Size);
                        
                        if (!this.currentFtueLevels.TryGetValue(FtueLevels.AutoUpdate.Name, out var autoUpdateLevel) || autoUpdateLevel < FtueLevels.AutoUpdate.AutoUpdateInitial)
                        {
                            if (DrawNextButton(State.AskAutoUpdate))
                            {
                                this.currentFtueLevels[FtueLevels.AutoUpdate.Name] = FtueLevels.AutoUpdate.AutoUpdateInitial;
                            }
                        }
                        else
                        {
                            DrawNextButton(State.Links);
                        }
                        
                        break;
                    
                    case State.AskAutoUpdate:
                        ImGuiHelpers.SafeTextColoredWrapped(ImGuiColors.DalamudWhite, "Dalamud 可以自动更新你的插件，确保你始终拥有最新的功能和错误修复。你可以在此处选择何时以及如何运行自动更新。");
                        ImGuiHelpers.ScaledDummy(2);
                        
                        ImGuiHelpers.SafeTextColoredWrapped(ImGuiColors.DalamudGrey, "你始终可以通过单击插件列表中的更新按钮来手动更新插件。你还可以通过右键单击特定插件并选择“始终自动更新”来选择更新特定插件。");
                        ImGuiHelpers.SafeTextColoredWrapped(ImGuiColors.DalamudGrey, "Dalamud 只会在你处于休闲状态下通知你更新。");
                        
                        ImGuiHelpers.ScaledDummy(15);
                        
                        bool DrawCenteredButton(string text, float height)
                        {
                            var buttonHeight = height * ImGuiHelpers.GlobalScale;
                            var buttonWidth = ImGui.CalcTextSize(text).X + 50 * ImGuiHelpers.GlobalScale;
                            ImGuiHelpers.CenterCursorFor((int)buttonWidth);

                            return ImGui.Button(text, new Vector2(buttonWidth, buttonHeight)) &&
                                   !this.isFadingOutForStateChange;
                        }

                        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DPSRed))
                        {
                            if (DrawCenteredButton("启用自动更新", 30))
                            { 
                                this.chosenAutoUpdateBehavior = AutoUpdateBehavior.UpdateMainRepo;
                                GoToNextState(State.Links);
                            }
                        }
                        
                        ImGuiHelpers.ScaledDummy(2);
                        
                        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1))
                        using (var buttonColor = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                        {
                            buttonColor.Push(ImGuiCol.Border, ImGuiColors.DalamudGrey3);
                            if (DrawCenteredButton("禁用自动更新", 25))
                            { 
                                this.chosenAutoUpdateBehavior = AutoUpdateBehavior.OnlyNotify;
                                GoToNextState(State.Links);
                            }
                        }
                        
                        break;
                    
                    case State.Links:
                        ImGui.TextWrapped("如果你注意到任何问题或需要帮助，请查看常见问题解答，并在需要帮助的情况下在我们的Discord上联系我们。");
                        ImGui.TextWrapped("祝你享受游戏和 Dalamud 的时光！");
                        
                        ImGuiHelpers.ScaledDummy(45);
                        
                        bool CenteredIconButton(FontAwesomeIcon icon, string text)
                        {
                            var buttonWidth = ImGuiComponents.GetIconButtonWithTextWidth(icon, text);
                            ImGuiHelpers.CenterCursorFor((int)buttonWidth);
                            return ImGuiComponents.IconButtonWithText(icon, text);
                        }
                        
                        if (CenteredIconButton(FontAwesomeIcon.Download, "Open Plugin Installer"))
                        {
                            Service<DalamudInterface>.Get().OpenPluginInstaller();
                            this.IsOpen = false;
                            Dismiss();
                        }
                        
                        ImGuiHelpers.ScaledDummy(5);
                        
                        ImGuiHelpers.CenterCursorFor(
                            (int)(ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Globe, "查看 FAQ") +
                            ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.LaughBeam, "加入我们的Discord") +
                            (5 * ImGuiHelpers.GlobalScale) + 
                            (ImGui.GetStyle().ItemSpacing.X * 4)));
                        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Globe, "查看 FAQ"))
                        {
                            Util.OpenLink("https://info.atmoomen.top/");
                        }
                        
                        ImGui.SameLine();
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.SameLine();
                        
                        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.LaughBeam, "加入我们的Discord"))
                        {
                            Util.OpenLink("https://discord.gg/dailyroutines");
                        }
                        
                        ImGuiHelpers.ScaledDummy(5);
                        
                        if (CenteredIconButton(FontAwesomeIcon.Heart, "支持我们"))
                        {
                            Util.OpenLink("https://info.atmoomen.top/");
                        }
                        
                        var buttonHeight = 30 * ImGuiHelpers.GlobalScale;
                        var buttonText = "关闭";
                        var buttonWidth = ImGui.CalcTextSize(buttonText).X + 40 * ImGuiHelpers.GlobalScale;
                        ImGui.SetCursorPosY(windowSize.Y - buttonHeight - (20 * ImGuiHelpers.GlobalScale));
                        ImGuiHelpers.CenterCursorFor((int)buttonWidth);
                
                        if (ImGui.Button(buttonText, new Vector2(buttonWidth, buttonHeight)))
                        {
                            this.IsOpen = false;
                            Dismiss();
                        }
                        
                        break;
                }
            }
            
            // Draw close button in the top right corner
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 100f);
            var btnAlpha = Math.Clamp(this.windowFade.EasedPoint.X - 0.5f, 0f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DPSRed.WithAlpha(btnAlpha).Desaturate(0.3f));
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudWhite.WithAlpha(btnAlpha));
            
            var childSize = ImGui.GetWindowSize();
            var closeButtonSize = 15 * ImGuiHelpers.GlobalScale;
            ImGui.SetCursorPos(new Vector2(childSize.X - closeButtonSize - (10 * ImGuiHelpers.GlobalScale), 10 * ImGuiHelpers.GlobalScale));
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
            {
                Dismiss();
                this.IsOpen = false;
            }

            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("我不关心这个");
            }
        }
    }

    /// <summary>
    /// Dispose this window.
    /// </summary>
    public void Dispose()
    {
        this.scopedFinalizer.Dispose();
    }
    
    private void FrameworkOnUpdate(IFramework unused)
    {
        if (!WarrantsChangelog())
            return;

        if (this.IsOpen)
            return;

        if (this.openedThroughEligibility)
            return;
        
        var isEligible = this.gameGui.GetAddonByName("_TitleMenu", 1) != IntPtr.Zero;

        var charaSelect = this.gameGui.GetAddonByName("CharaSelect", 1);
        var charaMake = this.gameGui.GetAddonByName("CharaMake", 1);
        var titleDcWorldMap = this.gameGui.GetAddonByName("TitleDCWorldMap", 1);
        if (charaMake != IntPtr.Zero || charaSelect != IntPtr.Zero || titleDcWorldMap != IntPtr.Zero)
            isEligible = false;

        if (this.isEligibleSince == null && isEligible)
        {
            this.isEligibleSince = DateTime.Now;
        }
        else if (this.isEligibleSince != null && !isEligible)
        {
            this.isEligibleSince = null;
        }
        
        if (this.isEligibleSince != null && DateTime.Now - this.isEligibleSince > TitleScreenWaitTime)
        {
            this.IsOpen = true;
            this.openedThroughEligibility = true;
        }
    }
    
    private unsafe void ResetMovieTimer()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var agentModule = uiModule->GetAgentModule();
        if (agentModule == null)
            return;

        var agentLobby = agentModule->GetAgentLobby();
        if (agentLobby == null)
            return;

        agentLobby->IdleTime = 0;
    }

    private static class FtueLevels
    {
        public static class AutoUpdate
        {
            public const string Name = "AutoUpdate";
            public const int AutoUpdateInitial = 1;
        }
    }
}
