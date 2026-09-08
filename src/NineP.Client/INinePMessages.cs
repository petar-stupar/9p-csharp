using NineP.Protocol;
using NineP.Protocol.Messages;

namespace NineP.Client;

/// <summary>
/// One method per legal T-message: the low-level API (workspace architecture §6). The tag is
/// chosen by the multiplexer, so the request a caller builds may leave it at zero. A method whose
/// message is illegal for the session dialect throws
/// <see cref="NinePProtocolException"/> with <see cref="ProtocolErrorKind.Type"/> <b>before</b>
/// anything reaches the wire.
/// </summary>
public interface INinePMessages
{
    /// <summary>Sends a <c>Tversion</c> and returns the <c>Rversion</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rversion> VersionAsync(Tversion request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tauth</c> and returns the <c>Rauth</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rauth> AuthAsync(Tauth request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tattach</c> and returns the <c>Rattach</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rattach> AttachAsync(Tattach request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tflush</c> and returns the <c>Rflush</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rflush> FlushAsync(Tflush request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Twalk</c> and returns the <c>Rwalk</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rwalk> WalkAsync(Twalk request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Topen</c> and returns the <c>Ropen</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Ropen> OpenAsync(Topen request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tcreate</c> and returns the <c>Rcreate</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rcreate> CreateAsync(Tcreate request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tread</c> and returns the <c>Rread</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rread> ReadAsync(Tread request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Twrite</c> and returns the <c>Rwrite</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rwrite> WriteAsync(Twrite request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tclunk</c> and returns the <c>Rclunk</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rclunk> ClunkAsync(Tclunk request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tremove</c> and returns the <c>Rremove</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rremove> RemoveAsync(Tremove request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tstat</c> and returns the <c>Rstat</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rstat> StatAsync(Tstat request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Twstat</c> and returns the <c>Rwstat</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rwstat> WstatAsync(Twstat request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tstatfs</c> and returns the <c>Rstatfs</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rstatfs> StatfsAsync(Tstatfs request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tlopen</c> and returns the <c>Rlopen</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rlopen> LopenAsync(Tlopen request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tlcreate</c> and returns the <c>Rlcreate</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rlcreate> LcreateAsync(Tlcreate request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tsymlink</c> and returns the <c>Rsymlink</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rsymlink> SymlinkAsync(Tsymlink request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tmknod</c> and returns the <c>Rmknod</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rmknod> MknodAsync(Tmknod request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Trename</c> and returns the <c>Rrename</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rrename> RenameAsync(Trename request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Treadlink</c> and returns the <c>Rreadlink</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rreadlink> ReadlinkAsync(Treadlink request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tgetattr</c> and returns the <c>Rgetattr</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rgetattr> GetattrAsync(Tgetattr request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tsetattr</c> and returns the <c>Rsetattr</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rsetattr> SetattrAsync(Tsetattr request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Txattrwalk</c> and returns the <c>Rxattrwalk</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rxattrwalk> XattrwalkAsync(Txattrwalk request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Txattrcreate</c> and returns the <c>Rxattrcreate</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rxattrcreate> XattrcreateAsync(Txattrcreate request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Treaddir</c> and returns the <c>Rreaddir</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rreaddir> ReaddirAsync(Treaddir request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tfsync</c> and returns the <c>Rfsync</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rfsync> FsyncAsync(Tfsync request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tlock</c> and returns the <c>Rlock</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rlock> LockAsync(Tlock request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tgetlock</c> and returns the <c>Rgetlock</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rgetlock> GetlockAsync(Tgetlock request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tlink</c> and returns the <c>Rlink</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rlink> LinkAsync(Tlink request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tmkdir</c> and returns the <c>Rmkdir</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rmkdir> MkdirAsync(Tmkdir request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Trenameat</c> and returns the <c>Rrenameat</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Rrenameat> RenameatAsync(Trenameat request, CancellationToken cancellationToken = default);

    /// <summary>Sends a <c>Tunlinkat</c> and returns the <c>Runlinkat</c> the server answered with.</summary>
    /// <param name="request">The request; its tag is replaced by the multiplexer's.</param>
    /// <param name="cancellationToken">Cancels the request, which sends a <c>Tflush</c>.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="NinePException">The server answered with an error.</exception>
    ValueTask<Runlinkat> UnlinkatAsync(Tunlinkat request, CancellationToken cancellationToken = default);
}
