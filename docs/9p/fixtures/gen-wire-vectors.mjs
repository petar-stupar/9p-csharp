#!/usr/bin/env node
/**
 * Reference wire encoder for 9P2000 / 9P2000.u / 9P2000.L.
 *
 * Emits docs/9p/fixtures/wire-vectors.json: one golden byte sequence per
 * message type (and per dialect where the layout differs), built straight
 * from the field layouts in docs/9p/protocol-reference.md. Every language
 * implementation decodes each vector, re-encodes it, and must reproduce the
 * bytes exactly; the codec round-trip suite in each repo is driven by this
 * file. Regenerate with `node docs/9p/fixtures/gen-wire-vectors.mjs`; the
 * output is deterministic and committed.
 *
 * Deliberately naive: no shared helpers with any implementation, no
 * dependency, one function per field type. Its only job is to be obviously
 * correct against the reference document.
 */
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const OUT = fileURLToPath(new URL('./wire-vectors.json', import.meta.url));

const NOTAG = 0xffff;
const NOFID = 0xffffffff;

// Message type numbers (docs/9p/protocol-reference.md §2).
const T = {
  Tlerror: 6, Rlerror: 7, Tstatfs: 8, Rstatfs: 9, Tlopen: 12, Rlopen: 13, Tlcreate: 14, Rlcreate: 15,
  Tsymlink: 16, Rsymlink: 17, Tmknod: 18, Rmknod: 19, Trename: 20, Rrename: 21, Treadlink: 22, Rreadlink: 23,
  Tgetattr: 24, Rgetattr: 25, Tsetattr: 26, Rsetattr: 27, Txattrwalk: 30, Rxattrwalk: 31,
  Txattrcreate: 32, Rxattrcreate: 33, Treaddir: 40, Rreaddir: 41, Tfsync: 50, Rfsync: 51,
  Tlock: 52, Rlock: 53, Tgetlock: 54, Rgetlock: 55, Tlink: 70, Rlink: 71, Tmkdir: 72, Rmkdir: 73,
  Trenameat: 74, Rrenameat: 75, Tunlinkat: 76, Runlinkat: 77,
  Tversion: 100, Rversion: 101, Tauth: 102, Rauth: 103, Tattach: 104, Rattach: 105, Terror: 106, Rerror: 107,
  Tflush: 108, Rflush: 109, Twalk: 110, Rwalk: 111, Topen: 112, Ropen: 113, Tcreate: 114, Rcreate: 115,
  Tread: 116, Rread: 117, Twrite: 118, Rwrite: 119, Tclunk: 120, Rclunk: 121, Tremove: 122, Rremove: 123,
  Tstat: 124, Rstat: 125, Twstat: 126, Rwstat: 127,
};

/** Little-endian field encoders. Each returns a Buffer. */
const u8 = (v) => Buffer.from([v & 0xff]);
const u16 = (v) => { const b = Buffer.alloc(2); b.writeUInt16LE(v); return b; };
const u32 = (v) => { const b = Buffer.alloc(4); b.writeUInt32LE(v >>> 0); return b; };
const u64 = (v) => { const b = Buffer.alloc(8); b.writeBigUInt64LE(BigInt(v)); return b; };
const str = (s) => { const d = Buffer.from(s, 'utf8'); return Buffer.concat([u16(d.length), d]); };
const data = (d) => Buffer.concat([u32(d.length), d]); // count[4] data[count]
const qid = ({ type, version, path }) => Buffer.concat([u8(type), u32(version), u64(path)]);

/** stat[n] as carried in Rstat/Twstat: n[2] then the record, whose own first field is size[2] = bytes after it. */
function statRecord(s, dotu) {
  const body = Buffer.concat([
    u16(s.type), u32(s.dev), qid(s.qid), u32(s.mode), u32(s.atime), u32(s.mtime), u64(s.length),
    str(s.name), str(s.uid), str(s.gid), str(s.muid),
    ...(dotu ? [str(s.extension), u32(s.n_uid), u32(s.n_gid), u32(s.n_muid)] : []),
  ]);
  return Buffer.concat([u16(body.length), body]); // size[2] + fields
}
const statField = (s, dotu) => { const r = statRecord(s, dotu); return Buffer.concat([u16(r.length), r]); };

/** A 9P2000.L directory entry: qid[13] offset[8] type[1] name[s]. */
const dirent = (e) => Buffer.concat([qid(e.qid), u64(e.offset), u8(e.type), str(e.name)]);

/** Frame: size[4] type[1] tag[2] body. size counts itself. */
function frame(type, tag, ...fields) {
  const body = Buffer.concat(fields);
  return Buffer.concat([u32(4 + 1 + 2 + body.length), u8(type), u16(tag), body]);
}

const QDIR = { type: 0x80, version: 0, path: 0x1n };
const QFILE = { type: 0x00, version: 3, path: 0x2a };
const QLINK = { type: 0x02, version: 0, path: 0x33 };
const QAUTH = { type: 0x08, version: 0, path: 0x7 };

const fileStat = {
  type: 0, dev: 0, qid: QFILE, mode: 0o644, atime: 1700000000, mtime: 1700000001, length: 6,
  name: 'label', uid: 'alice', gid: 'users', muid: 'alice', extension: '', n_uid: 1000, n_gid: 100, n_muid: 1000,
};
const symlinkStatU = {
  type: 0, dev: 0, qid: QLINK, mode: 0x02000000 | 0o777, atime: 1700000000, mtime: 1700000000, length: 0,
  name: 'link', uid: 'alice', gid: 'users', muid: 'alice', extension: '/target', n_uid: 1000, n_gid: 100, n_muid: 1000,
};
// "don't touch" wstat: ~0 integers, empty strings (stat(5)).
const dontTouch = {
  type: 0xffff, dev: 0xffffffff, qid: { type: 0xff, version: 0xffffffff, path: 0xffffffffffffffffn },
  mode: 0xffffffff, atime: 0xffffffff, mtime: 0xffffffff, length: 0xffffffffffffffffn,
  name: '', uid: '', gid: '', muid: '', extension: '', n_uid: 0xffffffff, n_gid: 0xffffffff, n_muid: 0xffffffff,
};

const hello = Buffer.from('hello\n', 'utf8');

const vectors = [
  // ---- 9P2000 core -----------------------------------------------------------------------
  ['Tversion', '9P2000', { tag: NOTAG, msize: 8192, version: '9P2000' }, frame(T.Tversion, NOTAG, u32(8192), str('9P2000'))],
  ['Rversion', '9P2000', { tag: NOTAG, msize: 8192, version: '9P2000' }, frame(T.Rversion, NOTAG, u32(8192), str('9P2000'))],
  ['Tversion', '9P2000.L', { tag: NOTAG, msize: 1048576, version: '9P2000.L' }, frame(T.Tversion, NOTAG, u32(1048576), str('9P2000.L'))],
  ['Rversion', 'unknown', { tag: NOTAG, msize: 8192, version: 'unknown' }, frame(T.Rversion, NOTAG, u32(8192), str('unknown'))],
  ['Tauth', '9P2000', { tag: 1, afid: 5, uname: 'alice', aname: '' }, frame(T.Tauth, 1, u32(5), str('alice'), str(''))],
  ['Tauth', '9P2000.u', { tag: 1, afid: 5, uname: 'alice', aname: '', n_uname: 1000 }, frame(T.Tauth, 1, u32(5), str('alice'), str(''), u32(1000))],
  ['Rauth', '9P2000', { tag: 1, aqid: QAUTH }, frame(T.Rauth, 1, qid(QAUTH))],
  ['Tattach', '9P2000', { tag: 2, fid: 0, afid: NOFID, uname: 'alice', aname: '' }, frame(T.Tattach, 2, u32(0), u32(NOFID), str('alice'), str(''))],
  ['Tattach', '9P2000.u', { tag: 2, fid: 0, afid: 5, uname: 'alice', aname: '/', n_uname: 1000 }, frame(T.Tattach, 2, u32(0), u32(5), str('alice'), str('/'), u32(1000))],
  ['Tattach', '9P2000.L', { tag: 2, fid: 0, afid: NOFID, uname: '', aname: '/', n_uname: 1000 }, frame(T.Tattach, 2, u32(0), u32(NOFID), str(''), str('/'), u32(1000))],
  ['Rattach', '9P2000', { tag: 2, qid: QDIR }, frame(T.Rattach, 2, qid(QDIR))],
  ['Rerror', '9P2000', { tag: 3, ename: 'file not found' }, frame(T.Rerror, 3, str('file not found'))],
  ['Rerror', '9P2000.u', { tag: 3, ename: 'file not found', errno: 2 }, frame(T.Rerror, 3, str('file not found'), u32(2))],
  ['Rlerror', '9P2000.L', { tag: 3, ecode: 2 }, frame(T.Rlerror, 3, u32(2))],
  ['Tflush', '9P2000', { tag: 4, oldtag: 3 }, frame(T.Tflush, 4, u16(3))],
  ['Rflush', '9P2000', { tag: 4 }, frame(T.Rflush, 4)],
  ['Twalk', '9P2000', { tag: 5, fid: 0, newfid: 1, wname: ['users', 'alice'] }, frame(T.Twalk, 5, u32(0), u32(1), u16(2), str('users'), str('alice'))],
  ['Twalk', '9P2000', { tag: 5, fid: 0, newfid: 1, wname: [] }, frame(T.Twalk, 5, u32(0), u32(1), u16(0))],
  ['Rwalk', '9P2000', { tag: 5, wqid: [QDIR, { type: 0x80, version: 0, path: 0x10 }] }, frame(T.Rwalk, 5, u16(2), qid(QDIR), qid({ type: 0x80, version: 0, path: 0x10 }))],
  ['Topen', '9P2000', { tag: 6, fid: 1, mode: 0 }, frame(T.Topen, 6, u32(1), u8(0))],
  ['Ropen', '9P2000', { tag: 6, qid: QFILE, iounit: 8168 }, frame(T.Ropen, 6, qid(QFILE), u32(8168))],
  ['Tcreate', '9P2000', { tag: 7, fid: 1, name: 'label', perm: 0o644, mode: 1 }, frame(T.Tcreate, 7, u32(1), str('label'), u32(0o644), u8(1))],
  ['Tcreate', '9P2000.u', { tag: 7, fid: 1, name: 'link', perm: 0x02000000 | 0o777, mode: 0, extension: '/target' }, frame(T.Tcreate, 7, u32(1), str('link'), u32(0x02000000 | 0o777), u8(0), str('/target'))],
  ['Rcreate', '9P2000', { tag: 7, qid: QFILE, iounit: 0 }, frame(T.Rcreate, 7, qid(QFILE), u32(0))],
  ['Tread', '9P2000', { tag: 8, fid: 1, offset: 0, count: 8168 }, frame(T.Tread, 8, u32(1), u64(0), u32(8168))],
  ['Rread', '9P2000', { tag: 8, data: 'hello\\n' }, frame(T.Rread, 8, data(hello))],
  ['Twrite', '9P2000', { tag: 9, fid: 1, offset: 6, data: 'hello\\n' }, frame(T.Twrite, 9, u32(1), u64(6), data(hello))],
  ['Rwrite', '9P2000', { tag: 9, count: 6 }, frame(T.Rwrite, 9, u32(6))],
  ['Tclunk', '9P2000', { tag: 10, fid: 1 }, frame(T.Tclunk, 10, u32(1))],
  ['Rclunk', '9P2000', { tag: 10 }, frame(T.Rclunk, 10)],
  ['Tremove', '9P2000', { tag: 11, fid: 1 }, frame(T.Tremove, 11, u32(1))],
  ['Rremove', '9P2000', { tag: 11 }, frame(T.Rremove, 11)],
  ['Tstat', '9P2000', { tag: 12, fid: 1 }, frame(T.Tstat, 12, u32(1))],
  ['Rstat', '9P2000', { tag: 12, stat: fileStat }, frame(T.Rstat, 12, statField(fileStat, false))],
  ['Rstat', '9P2000.u', { tag: 12, stat: symlinkStatU }, frame(T.Rstat, 12, statField(symlinkStatU, true))],
  ['Twstat', '9P2000', { tag: 13, fid: 1, stat: 'all-dont-touch (sync request)' }, frame(T.Twstat, 13, u32(1), statField(dontTouch, false))],
  ['Twstat', '9P2000.u', { tag: 13, fid: 1, stat: 'all-dont-touch (sync request)' }, frame(T.Twstat, 13, u32(1), statField(dontTouch, true))],
  ['Rwstat', '9P2000', { tag: 13 }, frame(T.Rwstat, 13)],
  // ---- 9P2000.L ---------------------------------------------------------------------------
  ['Tstatfs', '9P2000.L', { tag: 20, fid: 0 }, frame(T.Tstatfs, 20, u32(0))],
  ['Rstatfs', '9P2000.L', { tag: 20, type: 0x01021997, bsize: 4096, blocks: 1000, bfree: 500, bavail: 400, files: 100, ffree: 50, fsid: 7, namelen: 255 },
    frame(T.Rstatfs, 20, u32(0x01021997), u32(4096), u64(1000), u64(500), u64(400), u64(100), u64(50), u64(7), u32(255))],
  ['Tlopen', '9P2000.L', { tag: 21, fid: 1, flags: 0o2 }, frame(T.Tlopen, 21, u32(1), u32(0o2))],
  ['Rlopen', '9P2000.L', { tag: 21, qid: QFILE, iounit: 0 }, frame(T.Rlopen, 21, qid(QFILE), u32(0))],
  ['Tlcreate', '9P2000.L', { tag: 22, fid: 1, name: 'label', flags: 0o100101, mode: 0o644, gid: 100 }, frame(T.Tlcreate, 22, u32(1), str('label'), u32(0o100101), u32(0o644), u32(100))],
  ['Rlcreate', '9P2000.L', { tag: 22, qid: QFILE, iounit: 0 }, frame(T.Rlcreate, 22, qid(QFILE), u32(0))],
  ['Tsymlink', '9P2000.L', { tag: 23, fid: 0, name: 'link', symtgt: '/target', gid: 100 }, frame(T.Tsymlink, 23, u32(0), str('link'), str('/target'), u32(100))],
  ['Rsymlink', '9P2000.L', { tag: 23, qid: QLINK }, frame(T.Rsymlink, 23, qid(QLINK))],
  ['Tmknod', '9P2000.L', { tag: 24, dfid: 0, name: 'null', mode: 0o20666, major: 1, minor: 3, gid: 0 }, frame(T.Tmknod, 24, u32(0), str('null'), u32(0o20666), u32(1), u32(3), u32(0))],
  ['Rmknod', '9P2000.L', { tag: 24, qid: { type: 0, version: 0, path: 0x44 } }, frame(T.Rmknod, 24, qid({ type: 0, version: 0, path: 0x44 }))],
  ['Trename', '9P2000.L', { tag: 25, fid: 1, dfid: 0, name: 'renamed' }, frame(T.Trename, 25, u32(1), u32(0), str('renamed'))],
  ['Rrename', '9P2000.L', { tag: 25 }, frame(T.Rrename, 25)],
  ['Treadlink', '9P2000.L', { tag: 26, fid: 2 }, frame(T.Treadlink, 26, u32(2))],
  ['Rreadlink', '9P2000.L', { tag: 26, target: '/target' }, frame(T.Rreadlink, 26, str('/target'))],
  ['Tgetattr', '9P2000.L', { tag: 27, fid: 1, request_mask: 0x3fff }, frame(T.Tgetattr, 27, u32(1), u64(0x3fff))],
  ['Rgetattr', '9P2000.L', {
    tag: 27, valid: 0x7ff, qid: QFILE, mode: 0o100644, uid: 1000, gid: 100, nlink: 1, rdev: 0, size: 6, blksize: 4096, blocks: 8,
    atime_sec: 1700000000, atime_nsec: 0, mtime_sec: 1700000001, mtime_nsec: 500, ctime_sec: 1700000001, ctime_nsec: 500,
    btime_sec: 0, btime_nsec: 0, gen: 0, data_version: 0,
  }, frame(T.Rgetattr, 27, u64(0x7ff), qid(QFILE), u32(0o100644), u32(1000), u32(100), u64(1), u64(0), u64(6), u64(4096), u64(8),
    u64(1700000000), u64(0), u64(1700000001), u64(500), u64(1700000001), u64(500), u64(0), u64(0), u64(0), u64(0))],
  ['Tsetattr', '9P2000.L', { tag: 28, fid: 1, valid: 0x9, mode: 0o600, uid: 0, gid: 0, size: 0, atime_sec: 0, atime_nsec: 0, mtime_sec: 0, mtime_nsec: 0 },
    frame(T.Tsetattr, 28, u32(1), u32(0x9), u32(0o600), u32(0), u32(0), u64(0), u64(0), u64(0), u64(0), u64(0))],
  ['Rsetattr', '9P2000.L', { tag: 28 }, frame(T.Rsetattr, 28)],
  ['Txattrwalk', '9P2000.L', { tag: 29, fid: 1, newfid: 3, name: 'user.note' }, frame(T.Txattrwalk, 29, u32(1), u32(3), str('user.note'))],
  ['Rxattrwalk', '9P2000.L', { tag: 29, size: 5 }, frame(T.Rxattrwalk, 29, u64(5))],
  ['Txattrcreate', '9P2000.L', { tag: 30, fid: 3, name: 'user.note', attr_size: 5, flags: 0 }, frame(T.Txattrcreate, 30, u32(3), str('user.note'), u64(5), u32(0))],
  ['Rxattrcreate', '9P2000.L', { tag: 30 }, frame(T.Rxattrcreate, 30)],
  ['Treaddir', '9P2000.L', { tag: 31, fid: 1, offset: 0, count: 65488 }, frame(T.Treaddir, 31, u32(1), u64(0), u32(65488))],
  ['Rreaddir', '9P2000.L', { tag: 31, entries: [{ qid: QDIR, offset: 1, type: 4, name: 'users' }, { qid: QFILE, offset: 2, type: 8, name: 'auth' }] },
    frame(T.Rreaddir, 31, data(Buffer.concat([dirent({ qid: QDIR, offset: 1, type: 4, name: 'users' }), dirent({ qid: QFILE, offset: 2, type: 8, name: 'auth' })])))],
  ['Tfsync', '9P2000.L', { tag: 32, fid: 1, datasync: 0 }, frame(T.Tfsync, 32, u32(1), u32(0))],
  // The short Tfsync hugelgupf/p9 sends and expects (messages.go:1678-1692: fid only, no
  // datasync). Decoders must accept it and default datasync to 0; encoders never emit it.
  // protocol-reference.md 3.4.
  ['Tfsync (no datasync)', '9P2000.L', { tag: 32, fid: 1 }, frame(T.Tfsync, 32, u32(1))],
  ['Rfsync', '9P2000.L', { tag: 32 }, frame(T.Rfsync, 32)],
  ['Tlock', '9P2000.L', { tag: 33, fid: 1, type: 1, flags: 1, start: 0, length: 0, proc_id: 1234, client_id: 'host' }, frame(T.Tlock, 33, u32(1), u8(1), u32(1), u64(0), u64(0), u32(1234), str('host'))],
  ['Rlock', '9P2000.L', { tag: 33, status: 0 }, frame(T.Rlock, 33, u8(0))],
  ['Tgetlock', '9P2000.L', { tag: 34, fid: 1, type: 1, start: 0, length: 0, proc_id: 1234, client_id: 'host' }, frame(T.Tgetlock, 34, u32(1), u8(1), u64(0), u64(0), u32(1234), str('host'))],
  ['Rgetlock', '9P2000.L', { tag: 34, type: 2, start: 0, length: 0, proc_id: 0, client_id: '' }, frame(T.Rgetlock, 34, u8(2), u64(0), u64(0), u32(0), str(''))],
  ['Tlink', '9P2000.L', { tag: 35, dfid: 0, fid: 1, name: 'hard' }, frame(T.Tlink, 35, u32(0), u32(1), str('hard'))],
  ['Rlink', '9P2000.L', { tag: 35 }, frame(T.Rlink, 35)],
  ['Tmkdir', '9P2000.L', { tag: 36, dfid: 0, name: 'newdir', mode: 0o755, gid: 100 }, frame(T.Tmkdir, 36, u32(0), str('newdir'), u32(0o755), u32(100))],
  ['Rmkdir', '9P2000.L', { tag: 36, qid: { type: 0x80, version: 0, path: 0x55 } }, frame(T.Rmkdir, 36, qid({ type: 0x80, version: 0, path: 0x55 }))],
  ['Trenameat', '9P2000.L', { tag: 37, olddirfid: 0, oldname: 'a', newdirfid: 4, newname: 'b' }, frame(T.Trenameat, 37, u32(0), str('a'), u32(4), str('b'))],
  ['Rrenameat', '9P2000.L', { tag: 37 }, frame(T.Rrenameat, 37)],
  ['Tunlinkat', '9P2000.L', { tag: 38, dirfd: 0, name: 'newdir', flags: 0x200 }, frame(T.Tunlinkat, 38, u32(0), str('newdir'), u32(0x200))],
  ['Runlinkat', '9P2000.L', { tag: 38 }, frame(T.Runlinkat, 38)],
];

const out = {
  generated_by: 'docs/9p/fixtures/gen-wire-vectors.mjs',
  reference: 'docs/9p/protocol-reference.md',
  note: 'size[4] type[1] tag[2] then fields; all integers little-endian; strings are len[2]+UTF-8 without NUL. 64-bit values are given as decimal strings in fields.',
  // A vector name may carry a parenthesised variant suffix ("Tfsync (no datasync)"); the message
  // type is looked up from the first word.
  vectors: vectors.map(([name, dialect, fields, bytes]) => ({
    name, dialect, type: T[name.split(' ')[0]], size: bytes.length,
    fields: JSON.parse(JSON.stringify(fields, (k, v) => (typeof v === 'bigint' ? v.toString() : v))),
    hex: bytes.toString('hex'),
  })),
};
writeFileSync(OUT, `${JSON.stringify(out, null, 2)}\n`);
console.log(`wrote ${out.vectors.length} vectors to ${OUT}`);
