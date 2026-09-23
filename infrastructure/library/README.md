# Hispania.Library.LocalFileSystem

This adapter implements `IMediaDiscovery` for configured local filesystem roots and emits `.mp4`, `.mkv` and `.avi` files. Discovery does not claim playback compatibility; Direct Play remains a separate capability decision.

## Security policy

Each configured physical root is canonicalized with `Path.GetFullPath` at configuration time. Candidate paths are derived relative to that canonical root and are checked with `Path.GetRelativePath`; a candidate is rejected when it is rooted or escapes through a parent segment. This avoids prefix-based containment mistakes such as treating `C:\Media2` as a child of `C:\Media`.

The adapter has a conservative reparse-point policy: it rejects a configured root that is itself a reparse point and skips every reparse-point entry, whether it points inside or outside the root. It therefore never follows symbolic links, junctions, or other reparse points during discovery.

A missing or invalid configured root fails discovery clearly. Unreadable nested directories and files that disappear or become inaccessible during enumeration are skipped so the remaining root can continue. File metadata is a discovery-time observation only; later probe or streaming operations must revalidate it.
