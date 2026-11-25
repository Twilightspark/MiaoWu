using System.Resources;

namespace MiaoWu.App.Resources
{
    /// <summary>
    /// 用户可见文案的统一入口。V1 只有中文，后续可在此切换到多语言资源。
    /// </summary>
    public static class Strings
    {
        private static readonly ResourceManager Manager =
            new ResourceManager("MiaoWu.App.Resources.Strings", typeof(Strings).Assembly);

        public static string AppName => Get(nameof(AppName));

        public static string WindowMinimize => Get(nameof(WindowMinimize));

        public static string WindowMaximize => Get(nameof(WindowMaximize));

        public static string WindowRestore => Get(nameof(WindowRestore));

        public static string WindowClose => Get(nameof(WindowClose));

        public static string ToggleRightPaneCollapse => Get(nameof(ToggleRightPaneCollapse));

        public static string ToggleRightPaneExpand => Get(nameof(ToggleRightPaneExpand));

        public static string ToggleRightPaneHint => Get(nameof(ToggleRightPaneHint));

        public static string Workspace => Get(nameof(Workspace));

        public static string Sessions => Get(nameof(Sessions));

        public static string Files => Get(nameof(Files));

        public static string Pinned => Get(nameof(Pinned));

        public static string Today => Get(nameof(Today));

        public static string Yesterday => Get(nameof(Yesterday));

        public static string Earlier => Get(nameof(Earlier));

        public static string CapabilityMarket => Get(nameof(CapabilityMarket));

        public static string CapabilityMarketHint => Get(nameof(CapabilityMarketHint));

        public static string MarketCloseTooltip => Get(nameof(MarketCloseTooltip));

        public static string MarketSearchHint => Get(nameof(MarketSearchHint));

        public static string MarketImport => Get(nameof(MarketImport));

        public static string MarketOverview => Get(nameof(MarketOverview));

        public static string MarketBody => Get(nameof(MarketBody));

        public static string MarketTabsAutomation => Get(nameof(MarketTabsAutomation));

        public static string MarketCollapse => Get(nameof(MarketCollapse));

        public static string MarketActions => Get(nameof(MarketActions));

        public static string MarketOpenFolder => Get(nameof(MarketOpenFolder));

        public static string MarketOpenInEditor => Get(nameof(MarketOpenInEditor));

        public static string MarketCopyPath => Get(nameof(MarketCopyPath));

        public static string MarketRefresh => Get(nameof(MarketRefresh));

        public static string MarketOpenSkillsFolder => Get(nameof(MarketOpenSkillsFolder));

        public static string MarketOpenUserFolder => Get(nameof(MarketOpenUserFolder));

        public static string MarketImportFolder => Get(nameof(MarketImportFolder));

        public static string MarketEmptyImport => Get(nameof(MarketEmptyImport));

        public static string Settings => Get(nameof(Settings));

        public static string Refresh => Get(nameof(Refresh));

        public static string Find => Get(nameof(Find));

        public static string AddToConversation => Get(nameof(AddToConversation));

        public static string Search => Get(nameof(Search));

        public static string AddWorkspace => Get(nameof(AddWorkspace));

        public static string NewWorkspace => Get(nameof(NewWorkspace));

        public static string NewWorkspaceTooltip => Get(nameof(NewWorkspaceTooltip));

        public static string ScheduledTasks => Get(nameof(ScheduledTasks));

        public static string ScheduledTasksHint => Get(nameof(ScheduledTasksHint));

        public static string SchedulesCloseTooltip => Get(nameof(SchedulesCloseTooltip));

        public static string SchedulesFilterAutomation => Get(nameof(SchedulesFilterAutomation));

        public static string SchedulesTabsAutomation => Get(nameof(SchedulesTabsAutomation));

        public static string SchedulesSearchHint => Get(nameof(SchedulesSearchHint));

        public static string SchedulesNew => Get(nameof(SchedulesNew));

        public static string SchedulesRunNow => Get(nameof(SchedulesRunNow));

        public static string SchedulesDelete => Get(nameof(SchedulesDelete));

        public static string SchedulesOverwrite => Get(nameof(SchedulesOverwrite));

        public static string SchedulesReload => Get(nameof(SchedulesReload));

        public static string SchedulesNameLabel => Get(nameof(SchedulesNameLabel));

        public static string SchedulesContentLabel => Get(nameof(SchedulesContentLabel));

        public static string SchedulesDiscard => Get(nameof(SchedulesDiscard));

        public static string SchedulesSave => Get(nameof(SchedulesSave));

        public static string NewSession => Get(nameof(NewSession));

        public static string More => Get(nameof(More));

        public static string Pin => Get(nameof(Pin));

        public static string CloseTab => Get(nameof(CloseTab));

        public static string PendingApproval => Get(nameof(PendingApproval));

        public static string LeftPaneAutomation => Get(nameof(LeftPaneAutomation));

        public static string MiddlePaneAutomation => Get(nameof(MiddlePaneAutomation));

        public static string RightPaneAutomation => Get(nameof(RightPaneAutomation));

        public static string TopBarAutomation => Get(nameof(TopBarAutomation));

        public static string LeftSplitterAutomation => Get(nameof(LeftSplitterAutomation));

        public static string RightSplitterAutomation => Get(nameof(RightSplitterAutomation));

        public static string PlaceholderWorkspaceName => Get(nameof(PlaceholderWorkspaceName));

        public static string PlaceholderSecondaryWorkspace => Get(nameof(PlaceholderSecondaryWorkspace));

        public static string PlaceholderThirdWorkspace => Get(nameof(PlaceholderThirdWorkspace));

        public static string PlaceholderSessionPinned => Get(nameof(PlaceholderSessionPinned));

        public static string PlaceholderSessionPinnedTime => Get(nameof(PlaceholderSessionPinnedTime));

        public static string PlaceholderSession1 => Get(nameof(PlaceholderSession1));

        public static string PlaceholderSession1Time => Get(nameof(PlaceholderSession1Time));

        public static string PlaceholderSession2 => Get(nameof(PlaceholderSession2));

        public static string PlaceholderSession2Time => Get(nameof(PlaceholderSession2Time));

        public static string PlaceholderSession3 => Get(nameof(PlaceholderSession3));

        public static string PlaceholderSession3Time => Get(nameof(PlaceholderSession3Time));

        public static string PlaceholderSession4 => Get(nameof(PlaceholderSession4));

        public static string PlaceholderSession4Time => Get(nameof(PlaceholderSession4Time));

        public static string PlaceholderSession5 => Get(nameof(PlaceholderSession5));

        public static string PlaceholderSession5Time => Get(nameof(PlaceholderSession5Time));

        public static string PlaceholderDocumentTitle => Get(nameof(PlaceholderDocumentTitle));

        public static string PlaceholderDocumentMeta => Get(nameof(PlaceholderDocumentMeta));

        public static string PlaceholderDocumentSubtitle => Get(nameof(PlaceholderDocumentSubtitle));

        public static string PlaceholderDocumentHeading => Get(nameof(PlaceholderDocumentHeading));

        public static string PlaceholderDocumentBullet1 => Get(nameof(PlaceholderDocumentBullet1));

        public static string PlaceholderDocumentBullet2 => Get(nameof(PlaceholderDocumentBullet2));

        public static string PlaceholderDocumentBullet3 => Get(nameof(PlaceholderDocumentBullet3));

        public static string PlaceholderDocumentCodeHeading => Get(nameof(PlaceholderDocumentCodeHeading));

        public static string PlaceholderDocumentCode1 => Get(nameof(PlaceholderDocumentCode1));

        public static string PlaceholderDocumentCode2 => Get(nameof(PlaceholderDocumentCode2));

        public static string PlaceholderDocumentTableHeading => Get(nameof(PlaceholderDocumentTableHeading));

        public static string PlaceholderDocumentTableColumn1 => Get(nameof(PlaceholderDocumentTableColumn1));

        public static string PlaceholderDocumentTableColumn2 => Get(nameof(PlaceholderDocumentTableColumn2));

        public static string PlaceholderDocumentTableRow1 => Get(nameof(PlaceholderDocumentTableRow1));

        public static string PlaceholderDocumentTableRow2 => Get(nameof(PlaceholderDocumentTableRow2));

        public static string PlaceholderDocumentTableRow3 => Get(nameof(PlaceholderDocumentTableRow3));

        public static string PlaceholderDocumentFooter => Get(nameof(PlaceholderDocumentFooter));

        public static string PlaceholderTabDiff => Get(nameof(PlaceholderTabDiff));

        public static string PlaceholderTabCommand => Get(nameof(PlaceholderTabCommand));

        public static string PlaceholderConversationTitle => Get(nameof(PlaceholderConversationTitle));

        public static string PlaceholderMessageUser => Get(nameof(PlaceholderMessageUser));

        public static string PlaceholderMessageAssistant => Get(nameof(PlaceholderMessageAssistant));

        public static string PlaceholderMessageMeta => Get(nameof(PlaceholderMessageMeta));

        public static string PlaceholderModel => Get(nameof(PlaceholderModel));

        public static string PlaceholderUsage => Get(nameof(PlaceholderUsage));

        public static string PlaceholderInput => Get(nameof(PlaceholderInput));

        public static string PlaceholderSendHint => Get(nameof(PlaceholderSendHint));

        public static string AttachFile => Get(nameof(AttachFile));

        public static string AttachImage => Get(nameof(AttachImage));

        public static string Send => Get(nameof(Send));

        public static string PlaceholderConversationEmpty => Get(nameof(PlaceholderConversationEmpty));

        public static string UserInitial => Get(nameof(UserInitial));

        public static string AssistantInitial => Get(nameof(AssistantInitial));

        public static string PlaceholderImageName => Get(nameof(PlaceholderImageName));

        public static string PlaceholderProjectApp => Get(nameof(PlaceholderProjectApp));

        public static string PlaceholderProjectCore => Get(nameof(PlaceholderProjectCore));

        public static string PlaceholderProjectInfrastructure => Get(nameof(PlaceholderProjectInfrastructure));

        public static string PlaceholderSessionAudit => Get(nameof(PlaceholderSessionAudit));

        public static string PlaceholderSessionAuditTime => Get(nameof(PlaceholderSessionAuditTime));

        public static string PlaceholderSessionContext => Get(nameof(PlaceholderSessionContext));

        public static string PlaceholderSessionContextTime => Get(nameof(PlaceholderSessionContextTime));

        // ===== P2：左栏导航 =====

        public static string AddWorkspaceTooltip => Get(nameof(AddWorkspaceTooltip));

        public static string RemoveWorkspace => Get(nameof(RemoveWorkspace));

        public static string RemoveWorkspaceTooltip => Get(nameof(RemoveWorkspaceTooltip));

        public static string RemoveWorkspaceConfirmTitle => Get(nameof(RemoveWorkspaceConfirmTitle));

        public static string RemoveWorkspaceConfirmBody => Get(nameof(RemoveWorkspaceConfirmBody));

        public static string Remove => Get(nameof(Remove));

        public static string Cancel => Get(nameof(Cancel));

        public static string WorkspaceListEmptyTitle => Get(nameof(WorkspaceListEmptyTitle));

        public static string WorkspaceListEmptyBody => Get(nameof(WorkspaceListEmptyBody));

        public static string AddWorkspacePrimary => Get(nameof(AddWorkspacePrimary));

        public static string SessionsEmptyHint => Get(nameof(SessionsEmptyHint));

        public static string NewSessionPendingHint => Get(nameof(NewSessionPendingHint));

        public static string SearchPendingHint => Get(nameof(SearchPendingHint));

        public static string OpenFileTooltip => Get(nameof(OpenFileTooltip));

        public static string OpenFileDialogTitle => Get(nameof(OpenFileDialogTitle));

        public static string FilesEmptyHint => Get(nameof(FilesEmptyHint));

        public static string MarketplaceComingSoon => Get(nameof(MarketplaceComingSoon));

        public static string CapabilityMarketPendingTitle => Get(nameof(CapabilityMarketPendingTitle));

        public static string CapabilityMarketPendingBody => Get(nameof(CapabilityMarketPendingBody));

        public static string SettingsPendingTitle => Get(nameof(SettingsPendingTitle));

        public static string SettingsPendingBody => Get(nameof(SettingsPendingBody));

        public static string OverlayClose => Get(nameof(OverlayClose));

        public static string NewSessionInWorkspaceTooltip => Get(nameof(NewSessionInWorkspaceTooltip));

        public static string WorkspaceActions => Get(nameof(WorkspaceActions));

        public static string SessionActions => Get(nameof(SessionActions));

        public static string FileActions => Get(nameof(FileActions));

        public static string PinWorkspace => Get(nameof(PinWorkspace));

        public static string UnpinWorkspace => Get(nameof(UnpinWorkspace));

        public static string RenameWorkspace => Get(nameof(RenameWorkspace));

        public static string ClearWorkspaceSessions => Get(nameof(ClearWorkspaceSessions));

        public static string DeleteWorkspace => Get(nameof(DeleteWorkspace));

        public static string PinSession => Get(nameof(PinSession));

        public static string UnpinSession => Get(nameof(UnpinSession));

        public static string RenameSessionTitle => Get(nameof(RenameSessionTitle));

        public static string ArchiveSession => Get(nameof(ArchiveSession));

        public static string UnarchiveSession => Get(nameof(UnarchiveSession));

        public static string DeleteSession => Get(nameof(DeleteSession));

        public static string Archived => Get(nameof(Archived));

        public static string RenameFile => Get(nameof(RenameFile));

        public static string DeleteFile => Get(nameof(DeleteFile));

        public static string Delete => Get(nameof(Delete));

        public static string DeleteFileConfirmTitle => Get(nameof(DeleteFileConfirmTitle));

        public static string WorkspaceNameEmptyError => Get(nameof(WorkspaceNameEmptyError));

        public static string SessionTitleEmptyError => Get(nameof(SessionTitleEmptyError));

        // ===== P2：中栏画布 =====

        public static string DocumentEmptyTitle => Get(nameof(DocumentEmptyTitle));

        public static string DocumentEmptyBody => Get(nameof(DocumentEmptyBody));

        public static string OpenFilePrimary => Get(nameof(OpenFilePrimary));

        public static string AddToConversationPendingHint => Get(nameof(AddToConversationPendingHint));

        public static string FindPlaceholder => Get(nameof(FindPlaceholder));

        public static string FindNoMatches => Get(nameof(FindNoMatches));

        public static string FindPrev => Get(nameof(FindPrev));

        public static string FindNext => Get(nameof(FindNext));

        public static string Loading => Get(nameof(Loading));

        public static string Truncated => Get(nameof(Truncated));

        public static string BinaryPreviewTitle => Get(nameof(BinaryPreviewTitle));

        public static string BinaryPreviewBody => Get(nameof(BinaryPreviewBody));

        public static string DocumentErrorTitle => Get(nameof(DocumentErrorTitle));

        public static string Retry => Get(nameof(Retry));

        public static string CopyCode => Get(nameof(CopyCode));

        public static string Copied => Get(nameof(Copied));

        public static string DiffTabTitle => Get(nameof(DiffTabTitle));

        public static string CommandOutputTabTitle => Get(nameof(CommandOutputTabTitle));

        public static string ToolTabTitle => Get(nameof(ToolTabTitle));

        public static string DiffEmpty => Get(nameof(DiffEmpty));

        public static string CommandPrompt => Get(nameof(CommandPrompt));

        public static string SessionsListAutomation => Get(nameof(SessionsListAutomation));

        public static string FileTreeAutomation => Get(nameof(FileTreeAutomation));

        public static string TabsAutomation => Get(nameof(TabsAutomation));

        public static string CanvasAutomation => Get(nameof(CanvasAutomation));

        public static string AddTabTooltip => Get(nameof(AddTabTooltip));

        public static string FileMissingError => Get(nameof(FileMissingError));

        public static string DocumentScrollAutomation => Get(nameof(DocumentScrollAutomation));

        public static string FindCount(int count)
        {
            return string.Format(Get("FindCountFormat"), count);
        }

        public static string FindPosition(int position, int count)
        {
            return string.Format(Get("FindPositionFormat"), position, count);
        }

        public static string ExitCode(int exitCode)
        {
            return string.Format(Get("ExitCodeFormat"), exitCode);
        }

        public static string Duration(string duration)
        {
            return string.Format(Get("DurationFormat"), duration);
        }

        private static string Get(string key)
        {
            return Manager.GetString(key) ?? key;
        }
    }
}
