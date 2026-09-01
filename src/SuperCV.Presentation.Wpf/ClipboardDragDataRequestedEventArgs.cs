using System.Windows;

namespace SuperCV;

internal sealed class ClipboardDragDataRequestedEventArgs : EventArgs
{
    internal IDataObject? DataObject { get; set; }
}
