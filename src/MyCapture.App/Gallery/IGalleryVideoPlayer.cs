namespace MyCapture.App.Gallery;

/// <summary>The library's video-window operations, independently of the media backend.</summary>
internal interface IGalleryVideoPlayer
{
    Guid VideoId { get; }
    event EventHandler? Closed;
    bool Open(Guid id, string title, string path, bool autoPlay);
    void ConfigureFrameCapture(MyCapture.App.Recording.VideoFrameCaptureService service,
        long contentRevision, Action<Guid>? showInLibrary = null) { }
    void RequestPlay();
    void ShowPlayer(bool activate);
    void Close();
}
