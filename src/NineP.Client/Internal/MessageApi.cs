using NineP.Protocol.Messages;

namespace NineP.Client.Internal;

/// <summary>
/// The low-level API of §5.7, one method per legal T-message. Each one hands the multiplexer a
/// factory rather than a finished message, because the tag is the multiplexer's to choose:
/// only the client picks tags (reference §8 rule 12), and a caller that picked its own could
/// collide with a request already in flight.
/// </summary>
internal sealed class MessageApi(TagMultiplexer multiplexer, ClientOptions options) : INinePMessages
{
    /// <summary>The Tversion that opens a session carries NOTAG and takes no tag from the pool.</summary>
    /// <param name="request">The version proposal.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The server's answer.</returns>
    public ValueTask<Rversion> VersionAsync(Tversion request, CancellationToken cancellationToken = default) =>
        multiplexer.VersionAsync(request, cancellationToken);

    /// <summary>Sends a <c>Tauth</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rauth> AuthAsync(Tauth request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tauth, Rauth>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tattach</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rattach> AttachAsync(Tattach request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tattach, Rattach>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tflush</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rflush> FlushAsync(Tflush request, CancellationToken cancellationToken = default) =>
        multiplexer.FlushRequestAsync(request, cancellationToken);

    /// <summary>Sends a <c>Twalk</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rwalk> WalkAsync(Twalk request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Twalk, Rwalk>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Topen</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Ropen> OpenAsync(Topen request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Topen, Ropen>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tcreate</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rcreate> CreateAsync(Tcreate request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tcreate, Rcreate>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tread</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rread> ReadAsync(Tread request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tread, Rread>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Twrite</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rwrite> WriteAsync(Twrite request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Twrite, Rwrite>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tclunk</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rclunk> ClunkAsync(Tclunk request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tclunk, Rclunk>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tremove</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rremove> RemoveAsync(Tremove request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tremove, Rremove>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tstat</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rstat> StatAsync(Tstat request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tstat, Rstat>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Twstat</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rwstat> WstatAsync(Twstat request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Twstat, Rwstat>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tstatfs</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rstatfs> StatfsAsync(Tstatfs request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tstatfs, Rstatfs>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tlopen</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rlopen> LopenAsync(Tlopen request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tlopen, Rlopen>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tlcreate</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rlcreate> LcreateAsync(Tlcreate request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tlcreate, Rlcreate>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tsymlink</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rsymlink> SymlinkAsync(Tsymlink request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tsymlink, Rsymlink>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tmknod</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rmknod> MknodAsync(Tmknod request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tmknod, Rmknod>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Trename</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rrename> RenameAsync(Trename request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Trename, Rrename>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Treadlink</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rreadlink> ReadlinkAsync(Treadlink request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Treadlink, Rreadlink>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tgetattr</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rgetattr> GetattrAsync(Tgetattr request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tgetattr, Rgetattr>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tsetattr</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rsetattr> SetattrAsync(Tsetattr request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tsetattr, Rsetattr>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Txattrwalk</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rxattrwalk> XattrwalkAsync(Txattrwalk request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Txattrwalk, Rxattrwalk>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Txattrcreate</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rxattrcreate> XattrcreateAsync(Txattrcreate request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Txattrcreate, Rxattrcreate>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Treaddir</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rreaddir> ReaddirAsync(Treaddir request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Treaddir, Rreaddir>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tfsync</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rfsync> FsyncAsync(Tfsync request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tfsync, Rfsync>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tlock</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rlock> LockAsync(Tlock request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tlock, Rlock>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tgetlock</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rgetlock> GetlockAsync(Tgetlock request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tgetlock, Rgetlock>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tlink</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rlink> LinkAsync(Tlink request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tlink, Rlink>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tmkdir</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rmkdir> MkdirAsync(Tmkdir request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tmkdir, Rmkdir>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Trenameat</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Rrenameat> RenameatAsync(Trenameat request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Trenameat, Rrenameat>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>Sends a <c>Tunlinkat</c> under a tag of the multiplexer's choosing.</summary>
    /// <param name="request">The request; its tag is replaced.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The reply.</returns>
    public ValueTask<Runlinkat> UnlinkatAsync(Tunlinkat request, CancellationToken cancellationToken = default) =>
        multiplexer.RequestAsync<Tunlinkat, Runlinkat>(tag => request with { Tag = tag }, cancellationToken);

    /// <summary>The options the session was configured with, for the layers built on this one.</summary>
    internal ClientOptions Options => options;
}
