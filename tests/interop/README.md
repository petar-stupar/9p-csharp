# Interop peers

`InteropTests` in `tests/NineP.Client.Tests` runs this implementation against other 9P
implementations. Every test is an opt-in: it skips, naming the variable it wants, unless the
environment points it at a peer. [`setup.sh`](setup.sh) fetches or builds the peers at pinned
versions and prints those variables; [`diod.Dockerfile`](diod.Dockerfile) is the diod container it
builds. The measured results are in [docs/interop.md](../../docs/interop.md).

| variable | peer | role | what it points at |
| --- | --- | --- | --- |
| `NINEP_INTEROP_P9UFS` | hugelgupf/p9 `p9ufs` | 9P2000.L server | the `p9ufs` binary |
| `NINEP_INTEROP_PLAN9PORT` | plan9port `9p` | 9P2000 client | the plan9port root (`bin/9p` under it) |
| `NINEP_INTEROP_DIOD_IMAGE` | diod | 9P2000.L server | a docker image with `diod` on the path |
| `NINEP_INTEROP_LIMA_VM` | Linux v9fs | kernel client, all three dialects | a lima VM whose kernel has the `9p` module |
| `NINEP_INTEROP_DOCKER_HOST_ADDR` | — | — | optional: the address a published-port connection carries inside a container, when it is neither `192.168.65.1` (Docker Desktop) nor `172.17.0.1` (Linux) |

```text
eval "$(tests/interop/setup.sh)"
dotnet test --project tests/NineP.Client.Tests -f net10.0 -- --filter-class NineP.Client.Tests.Compat.InteropTests
```

## The enames Linux v9fs accepts over plain 9P2000

Over 9P2000 an `Rerror` carries only the ename, and the kernel maps it with the exact-match table
in `net/9p/error.c` (Plan 9 wordings and glibc `strerror` texts); anything else becomes
`ESERVERFAULT` (526). Per errno of `ErrorTable`: what this implementation sends, whether Linux
knows it, and every string Linux would accept for that errno. Extracted from the kernel source on
2026-09-10; the wording decision is the owner's and applies to every port (see
[docs/interop.md](../../docs/interop.md)).

| errno | this implementation sends | Linux | strings Linux maps to this errno |
| --- | --- | --- | --- |
| `EPERM` | `permission denied` | maps to EACCES | `Operation not permitted`, `wstat prohibited`, `wstat can't convert between files and directories`, `not a member of proposed group`, `no access to special file`, `only support truncation to zero length`, `cannot remove root` |
| `ENOENT` | `file not found` | exact | `No such file or directory`, `directory entry not found`, `file not found`, `file does not exist`, `illegal path element`, `directory entry is not allocated` |
| `EIO` | `i/o error` | exact | `Input/output error`, `i/o error`, `i/o count too large`, `corrupted directory entry`, `corrupted file entry`, `corrupted block label`, `corrupted meta data`, `root of file system is corrupted`, `corrupted super block`, `venti i/o error` |
| `ENXIO` | `no such device or address` | unknown to Linux (526) | `No such device or address` |
| `EBADF` | `unknown fid` | unknown to Linux (526) | `Bad file descriptor`, `fid unknown or out of range`, `bad use of fid`, `fid already in use` |
| `EAGAIN` | `try again` | unknown to Linux (526) | `Resource temporarily unavailable`, `exclusive use file already open`, `file is in use` |
| `ENOMEM` | `out of memory` | unknown to Linux (526) | `Cannot allocate memory` |
| `EACCES` | `permission denied` | exact | `Permission denied`, `permission denied`, `not owner`, `only owner can change group in wstat` |
| `EEXIST` | `file already exists` | exact | `File exists`, `file exists`, `file already exists`, `file or directory already exists` |
| `ENOTDIR` | `not a directory` | exact | `Not a directory`, `not a directory` |
| `EISDIR` | `is a directory` | unknown to Linux (526) | `Is a directory` |
| `EINVAL` | `bad argument` | unknown to Linux (526) | `Invalid argument`, `illegal mode`, `unknown group`, `unknown user`, `illegal offset` |
| `ENFILE` | `too many fids` | unknown to Linux (526) | `Too many open files in system` |
| `EFBIG` | `file too big` | exact | `File too large`, `file too big` |
| `ENOSPC` | `no space left` | unknown to Linux (526) | `No space left on device`, `file system is full` |
| `EROFS` | `read-only file system` | unknown to Linux (526) | `Read-only file system`, `read only file system`, `file is read only` |
| `ERANGE` | `result too large` | unknown to Linux (526) | `Numerical result out of range` |
| `ENAMETOOLONG` | `file name too long` | unknown to Linux (526) | `File name too long`, `illegal name` |
| `ENOLCK` | `lock not available` | unknown to Linux (526) | `No locks available` |
| `ENOSYS` | `not implemented` | unknown to Linux (526) | `Function not implemented` |
| `ENOTEMPTY` | `directory not empty` | unknown to Linux (526) | `Directory not empty`, `directory is not empty` |
| `ELOOP` | `too many symbolic links` | unknown to Linux (526) | `Too many levels of symbolic links` |
| `ENODATA` | `no such attribute` | unknown to Linux (526) | `No data available` |
| `EPROTO` | `bad message` | unknown to Linux (526) | `Protocol error`, `bogus wstat buffer`, `protocol botch` |
| `EOVERFLOW` | `value too large` | unknown to Linux (526) | — |
| `EOPNOTSUPP` | `not supported` | unknown to Linux (526) | `Operation not supported` |
| `ECONNREFUSED` | `authentication not required` | unknown to Linux (526) | `Connection refused`, `authentication failed` |
