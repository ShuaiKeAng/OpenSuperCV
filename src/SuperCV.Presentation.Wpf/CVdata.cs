using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SuperCV.Domain.Clipboard;
using SuperCV.Domain.History;

namespace SuperCV
{
    public class CVdata : ClipboardDataBase
    {
        private sealed class RevokeState
        {
            public RevokeState(Dictionary<TextFormat, string>? allTextList, int tag, bool topMost)
            {
                AllTextList = allTextList != null
                    ? new Dictionary<TextFormat, string>(allTextList)
                    : new Dictionary<TextFormat, string>();
                Tag = tag;
                TopMost = topMost;
            }

            public Dictionary<TextFormat, string> AllTextList { get; }
            public int Tag { get; }
            public bool TopMost { get; }
        }

        private const int MaxRevokeCount = 5;
        private readonly List<RevokeState> _revokeHistory = new();
        private CV? ItemUI;
        private ClipboardEntry? _entry;
        private bool _legacyFormatsMaterialized;

        internal bool HasOpenPopup => ItemUI?.HasOpenPopup == true;

        public Guid Id { get; private set; } = Guid.NewGuid();

        public DateTimeOffset CapturedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// The legacy window API requires a mutable dictionary, but keeping one for every history
        /// item duplicates all large strings.  Keep the immutable domain entry until a legacy
        /// operation actually asks for its formats.
        /// </summary>
        public override Dictionary<TextFormat, string> AllTextList
        {
            get
            {
                MaterializeLegacyFormats();
                return base.AllTextList;
            }
            set
            {
                _entry = null;
                _legacyFormatsMaterialized = true;
                base.AllTextList = value;
            }
        }

        /// <summary>
        /// Estimates the token count of this entry's Unicode text by using a
        /// Unicode-aware, model-independent BPE approximation.
        /// </summary>
        public int GetUnicodeTextTokenCount() =>
            WordBasedTokenEstimator.EstimateTokenCount(GetUnicodeText());

        /// <summary>
        /// Returns a prefix of this entry's Unicode text that fits within the
        /// approximate token limit. The entry itself is not modified.
        /// </summary>
        public string GetTruncatedUnicodeText(int maxTokens = 512) =>
            WordBasedTokenEstimator.TruncateToTokenCount(GetUnicodeText(), maxTokens);

        public CVdata(
            Dictionary<TextFormat, string>? allTextList = null,
            string? launchTime = null,
            int tag = 0,
            bool topMost = false)
            : base(
                allTextList ?? new Dictionary<TextFormat, string>(),
                launchTime ?? string.Empty,
                tag,
                topMost)
        {
            if (DateTimeOffset.TryParse(launchTime, out DateTimeOffset parsed))
            {
                CapturedAtUtc = parsed.ToUniversalTime();
            }
        }

        public void CVdata_Init(
            string? launchTime,
            Dictionary<TextFormat, string>? allText = null,
            int tag = 0,
            bool topMost = false)
        {
            LaunchTime = launchTime ?? string.Empty;
            CapturedAtUtc = DateTimeOffset.TryParse(launchTime, out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : DateTimeOffset.UtcNow;
            AllTextList = allText ?? new Dictionary<TextFormat, string>();
            this.Tag = tag;
            this.TopMost = topMost;
        }

        internal static CVdata FromEntry(ClipboardEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var item = new CVdata();
            item.SetEntry(entry);
            return item;
        }

        internal void ApplyEntry(ClipboardEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Id != Id)
            {
                throw new InvalidOperationException("Cannot apply another history entry to this view model.");
            }

            SetEntry(entry);
            if (ItemUI is not null)
            {
                ItemUI.UpdateCapturedAtUtc(entry.CapturedAtUtc);
                ItemUI.UpdateContent(CVText ?? string.Empty, ImageLink);
                ItemUI.Tag = Tag;
                ItemUI.TopIcon = TopMost ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        internal ClipboardEntry ToEntry()
        {
            if (_entry is not null)
            {
                return _entry with
                {
                    CapturedAtUtc = CapturedAtUtc,
                    Tag = Tag,
                    IsPinned = TopMost,
                };
            }

            return new ClipboardEntry(
                Id,
                CapturedAtUtc,
                new ClipboardPayload(AllTextList.ToDictionary(
                pair => pair.Key switch
                {
                    TextFormat.Text => ClipboardFormat.Text,
                    TextFormat.UnicodeText => ClipboardFormat.UnicodeText,
                    TextFormat.Html => ClipboardFormat.Html,
                    TextFormat.Rtf => ClipboardFormat.Rtf,
                    TextFormat.Image => ClipboardFormat.Image,
                    _ => throw new ArgumentOutOfRangeException(nameof(AllTextList)),
                },
                pair => pair.Value)),
                Tag,
                TopMost);
        }

        private string? CVText
        {
            get
            {
                if (AllTextList.TryGetValue(TextFormat.UnicodeText, out string? unicodeText))
                    return unicodeText;
                if (AllTextList.TryGetValue(TextFormat.Text, out string? text))
                    return text;
                return AllTextList
                    .Where(pair => pair.Key != TextFormat.Image)
                    .Select(pair => pair.Value)
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value));
            }
        }

        private string? GetUnicodeText() =>
            AllTextList.TryGetValue(TextFormat.UnicodeText, out string? unicodeText)
                ? unicodeText
                : null;

        /// <summary>
        /// Indicates whether this clipboard entry contains an image without forcing deferred
        /// clipboard formats to materialize.
        /// </summary>
        public bool IsImage => _entry?.Payload.IsImage ?? AllTextList.ContainsKey(TextFormat.Image);

        private string? ImageLink =>
            AllTextList.TryGetValue(TextFormat.Image, out string? link) ? link : null;



        private void UpdataUI(AnimationDirection appearanceDirection)
        {
            if (ItemUI is null)
            {
                CV? itemUi = CVListControl.RentReusableWindow();
                bool isReused = itemUi is not null;
                itemUi ??= new CV(
                    CVListControl.GetOwner(),
                    CapturedAtUtc,
                    CVText ?? string.Empty,
                    Tag,
                    TopMost,
                    ImageLink);
                if (isReused)
                {
                    itemUi.PrepareForReuse(
                        CapturedAtUtc,
                        CVText ?? string.Empty,
                        Tag,
                        TopMost,
                        ImageLink);
                }

                ItemUI = itemUi;
                itemUi.CanRevoke = _revokeHistory.Count > 0;
                AttachItemUi(itemUi);
                int realIndex = CVListControl.ListAll.IndexOf(this);
                itemUi.UpDataIndex(
                    realIndex,
                    selectedIndex: CVListControl.SelectedIndexes.IndexOf(realIndex),
                    indexUI: CVListControl.ListNow.IndexOf(this)
                );

                try
                {
                    if (isReused)
                    {
                        itemUi.ShowReused(appearanceDirection);
                    }
                    else
                    {
                        itemUi.Show(appearanceDirection);
                    }
                }
                catch
                {
                    DetachItemUi(itemUi);
                    ItemUI = null;
                    itemUi.CloseUI(AnimationDirection.None);
                    throw;
                }
            }
            else
            {
                int realIndex = CVListControl.ListAll.IndexOf(this);
                ItemUI.UpDataIndex(
                    realIndex,
                    selectedIndex: CVListControl.SelectedIndexes.IndexOf(realIndex),
                    indexUI: CVListControl.ListNow.IndexOf(this)
                );
                ItemUI.BiasTop = CVListControl.WheelStill;
            }
        }

        private void AttachItemUi(CV itemUi)
        {
            itemUi.EntryId = Id;
            itemUi.UnicodeTextTokenCountProvider = GetUnicodeTextTokenCount;
            itemUi.CVChanged += OnCVChanged;
            itemUi.CustomCommand += CustomCommand;
            itemUi.CVPaste += OnPaste;
            itemUi.DragDataRequested += OnDragDataRequested;
            itemUi.Closed += OnItemUiClosed;
        }

        private void OnCVChanged(object? sender, EventArgs e)
        {
            if (IsImage)
            {
                return;
            }

            if (ItemUI is not null && ItemUI.CVContent != CVText)
            {
                var newAllTextList = new Dictionary<TextFormat, string>
                {
                    [TextFormat.UnicodeText] = ItemUI.CVContent
                };
                try
                {
                    TryApplyState(new RevokeState(newAllTextList, Tag, TopMost));
                }
                catch (Exception exception)
                {
                    ItemUI.CVContent = CVText ?? string.Empty;
                    new AlertDialog($"保存修改失败：{exception.Message}").ShowDialog();
                }
            }
        }

        private void OnItemUiClosed(object? sender, EventArgs e)
        {
            if (sender is not CV itemUi || !ReferenceEquals(ItemUI, itemUi))
            {
                return;
            }

            DetachItemUi(itemUi);
            ItemUI = null;
            ReleaseLegacyFormats();
        }

        private void DetachItemUi(CV itemUi)
        {
            itemUi.UnicodeTextTokenCountProvider = null;
            itemUi.CVPaste -= OnPaste;
            itemUi.DragDataRequested -= OnDragDataRequested;
            itemUi.CVChanged -= OnCVChanged;
            itemUi.CustomCommand -= CustomCommand;
            itemUi.Closed -= OnItemUiClosed;
        }

        public void CloseUI(AnimationDirection direction)
        {
            if (ItemUI is not null)
            {
                CV itemUi = ItemUI;
                ItemUI = null;
                DetachItemUi(itemUi);
                if (itemUi.TryCloseForReuse(direction))
                {
                    ReleaseLegacyFormats();
                    return;
                }

                itemUi.CloseUI(direction);
                ReleaseLegacyFormats();
            }
        }

        internal void CloseUIImmediately()
        {
            if (ItemUI is null)
            {
                return;
            }

            CV itemUi = ItemUI;
            ItemUI = null;
            DetachItemUi(itemUi);
            itemUi.CloseUI(AnimationDirection.None);
            ReleaseLegacyFormats();
        }

        internal void UpdateVisibleUI(AnimationDirection appearanceDirection)
        {
            UpdataUI(appearanceDirection);
        }

        internal void ApplyTextSize(double textSize)
        {
            ItemUI?.ApplyTextSize(textSize);
        }

        // 重写基类的 OnPaste 方法，添加额外的延迟逻辑
        public override async void OnPaste(object? sender, EventArgs e)
        {
            try
            {
                var pasteArgs = e as BoolEventArgs;
                await PasteCoreAsync(
                    sender ?? this,
                    e,
                    pasteArgs?.Value == true,
                    pasteDelay: TimeSpan.FromMilliseconds(100),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                new AlertDialog($"粘贴失败：{exception.Message}").ShowDialog();
            }
        }

        internal Task PasteFromHotkeyAsync(CancellationToken cancellationToken) =>
            PasteCoreAsync(
                this,
                new BoolEventArgs(true),
                performPaste: true,
                pasteDelay: TimeSpan.Zero,
                cancellationToken);

        private async Task PasteCoreAsync(
            object sender,
            EventArgs e,
            bool performPaste,
            TimeSpan pasteDelay,
            CancellationToken cancellationToken)
        {
            CVListControl.NotifyPasteRequested(this);
            if (performPaste)
            {
                CVListControl.GetOwner().StartPulseAnimation();
            }

            base.OnPaste(sender, e);
            if (performPaste)
            {
                if (pasteDelay > TimeSpan.Zero)
                {
                    await Task.Delay(pasteDelay, cancellationToken);
                }

                await MyClipboard.PasteWithMonitoringSuspendedAsync();
            }
        }

        private void OnDragDataRequested(object? sender, ClipboardDragDataRequestedEventArgs e)
        {
            e.DataObject = ClipboardDragDataObjectFactory.Create(AllTextList);
        }

        private void CustomCommand(object? sender, string s)
        {
            try
            {
                ExecuteCustomCommand(s);
            }
            catch (Exception exception)
            {
                new AlertDialog($"操作失败：{exception.Message}").ShowDialog();
            }
        }

        private void ExecuteCustomCommand(string s)
        {
            if (IsImage &&
                s is "bookmark" or "onlyText" or "charExchange" or "revoke")
            {
                return;
            }

            switch (s)
            {
                case string digit when digit.Length == 1 && digit.All(char.IsDigit):
                    // 处理单个数字 0-9
                    int num = int.Parse(digit);
                    TryApplyState(new RevokeState(AllTextList, num, TopMost));
                    break;
                case "bookmark":
                    var dialog = new TextBox2Dialog(
                        title: "设置书签",
                        tip1: "请输入标签名",
                        tip2: "请输入内容",
                        prompt: CVText ?? string.Empty);
                    if (dialog.ShowDialog() == true)
                    {
                        // 检查条件
                        if (string.Equals(CVText, dialog.Prompt, StringComparison.Ordinal))
                        {
                            // 如果相同，使用 AllTextList 作为 text 参数
                            BookmarksControl.AddBookmark(
                                title: dialog.LabelName,
                                text: AllTextList,
                                launchTime: LaunchTime
                            );
                        }
                        else
                        {
                            var _text = new Dictionary<TextFormat, string>();
                            _text[TextFormat.UnicodeText] = dialog.Prompt ?? string.Empty;
                            // 如果不相同，可能需要使用其他值
                            // 这里根据您的需求调整，例如使用 dialog 中的文本内容
                            BookmarksControl.AddBookmark(
                                title: dialog.LabelName,
                                text: _text,
                                launchTime: LaunchTime
                            );
                        }
                    }

                    break;

                case "export":
                    if (ItemUI is not null)
                    {
                        ClipboardDocumentExporter.ShowExportDialog(
                            ItemUI,
                            AllTextList,
                            CapturedAtUtc);
                    }

                    break;

                case "onlyText":
                    string text = CVText ?? string.Empty;

                    var onlyTextList = new Dictionary<TextFormat, string>
                    {
                        [TextFormat.UnicodeText] = text
                    };
                    TryApplyState(new RevokeState(onlyTextList, Tag, TopMost));

                    break;

                case "top":
                    //LaunchTime = DateTime.Now.ToString();

                    //var item = CVListControl.ListAll[CVListControl.ListAll.IndexOf(this)];
                    //CVListControl.ListAll.Remove(item);
                    //CVListControl.ListAll.Insert(0, item);
                    TryApplyState(new RevokeState(AllTextList, Tag, !TopMost));

                    break;
                case "charExchange":
                    var dialogchar = new TextBox2Dialog(title: "查找替换", tip1: "查找原始字符", tip2: "替换为", prompt: "",allowNull: true);
                    if (dialogchar.ShowDialog() == true)
                    {
                        if (dialogchar.LabelName == "")
                        {
                            break;
                        }
                        string ori = dialogchar.LabelName;
                        string newText = CVText ?? string.Empty;
                        int count = newText.Split(new string[] { ori }, StringSplitOptions.None).Length - 1;
                        newText = newText.Replace(ori, dialogchar.Prompt ?? string.Empty);

                        var newAllTextList = new Dictionary<TextFormat, string>
                        {
                            [TextFormat.UnicodeText] = newText
                        };
                        TryApplyState(new RevokeState(newAllTextList, Tag, TopMost));
                        var result= count==0 ? $"" : ",已全部替换成功";
                        AlertDialog alertDialog = new AlertDialog($"找到“{ori}”共{count}处{result}");
                        alertDialog.Show();

                    }

                    break;
                case "revoke":
                    RevokeLastState();
                    break;

            }




        }

        private void RevokeLastState()
        {
            if (_revokeHistory.Count == 0)
            {
                return;
            }

            RevokeState state = _revokeHistory[^1];
            ApplyState(state);
            _revokeHistory.RemoveAt(_revokeHistory.Count - 1);
            UpdateRevokeAvailability();
        }

        private bool TryApplyState(RevokeState newState)
        {
            RevokeState currentState = CreateCurrentState();
            if (AreStatesEqual(currentState, newState))
            {
                return false;
            }

            ApplyState(newState);
            PushRevokeState(currentState);
            return true;
        }

        private void PushRevokeState(RevokeState state)
        {
            if (_revokeHistory.Count > 0 && AreStatesEqual(_revokeHistory[^1], state))
            {
                return;
            }

            _revokeHistory.Add(state);
            if (_revokeHistory.Count > MaxRevokeCount)
            {
                _revokeHistory.RemoveAt(0);
            }

            UpdateRevokeAvailability();
        }

        private void UpdateRevokeAvailability()
        {
            if (ItemUI is not null)
            {
                ItemUI.CanRevoke = _revokeHistory.Count > 0;
            }
        }

        private RevokeState CreateCurrentState()
        {
            return new RevokeState(AllTextList, Tag, TopMost);
        }

        private void ApplyState(RevokeState state)
        {
            RevokeState previousState = CreateCurrentState();
            ApplyStateToView(state);
            try
            {
                CVListControl.NotifyChanged(this);
            }
            catch
            {
                ApplyStateToView(previousState);
                throw;
            }
        }

        private void ApplyStateToView(RevokeState state)
        {
            AllTextList = new Dictionary<TextFormat, string>(state.AllTextList);
            Tag = state.Tag;
            TopMost = state.TopMost;

            if (ItemUI is not null)
            {
                ItemUI.UpdateContent(CVText ?? string.Empty, ImageLink);
                ItemUI.Tag = Tag;
                ItemUI.TopIcon = TopMost ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static bool AreStatesEqual(RevokeState left, RevokeState right)
        {
            if (left.Tag != right.Tag || left.TopMost != right.TopMost)
            {
                return false;
            }

            if (left.AllTextList.Count != right.AllTextList.Count)
            {
                return false;
            }

            foreach (var pair in left.AllTextList)
            {
                if (!right.AllTextList.TryGetValue(pair.Key, out string? value) || value != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private void SetEntry(ClipboardEntry entry)
        {
            _entry = entry;
            _legacyFormatsMaterialized = false;
            Id = entry.Id;
            CapturedAtUtc = entry.CapturedAtUtc;
            LaunchTime = entry.CapturedAtUtc.ToLocalTime().ToString();
            Tag = entry.Tag;
            TopMost = entry.IsPinned;
            base.AllTextList = new Dictionary<TextFormat, string>();
        }

        private void MaterializeLegacyFormats()
        {
            if (_legacyFormatsMaterialized || _entry is null)
            {
                return;
            }

            base.AllTextList = ToLegacyFormats(_entry.Payload);
            _legacyFormatsMaterialized = true;
        }

        private void ReleaseLegacyFormats()
        {
            if (_entry is null || !_legacyFormatsMaterialized)
            {
                return;
            }

            base.AllTextList = new Dictionary<TextFormat, string>();
            _legacyFormatsMaterialized = false;
        }

        private static Dictionary<TextFormat, string> ToLegacyFormats(ClipboardPayload payload) =>
            payload.Formats.ToDictionary(
                pair => pair.Key switch
                {
                    ClipboardFormat.Text => TextFormat.Text,
                    ClipboardFormat.UnicodeText => TextFormat.UnicodeText,
                    ClipboardFormat.Html => TextFormat.Html,
                    ClipboardFormat.Rtf => TextFormat.Rtf,
                    ClipboardFormat.Image => TextFormat.Image,
                    _ => throw new ArgumentOutOfRangeException(nameof(payload)),
                },
                pair => pair.Value);
    }


    public class BoolEventArgs : EventArgs
    {
        public bool Value { get; set; }

        public BoolEventArgs(bool value)
        {
            Value = value;
        }
    }
}
