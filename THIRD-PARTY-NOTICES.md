# Third-party notices

FrameReader (MIT) contains no third-party code: it calls FFmpeg's shared libraries through FFmpeg.AutoGen's bindings
(MIT), loaded dynamically at run time. Those libraries, built by us (`tools/native/build-ffmpeg.sh`), ship in the
FrameReader.Native.<rid> packages, with the libraries below linked into them.

## FFmpeg libraries (GNU LGPL 2.1 or later), FrameReader.Native.<rid>

libavcodec, libavformat, libavutil, libswscale, libswresample and libavfilter (yadif, fps, aspectralstats and the audio filters listed in the Native README only) from FFmpeg 8.1.3
(https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz), built with `tools/native/build-ffmpeg.sh` for decoding plus
the AAC, MP3 and PCM encoders, the image encoders (PNG, JPEG, WebP), FrameReader's test encoders and common
muxers: no other video encoders, devices or GPL parts, and only the http(s)
network protocols; zlib, dav1d, LAME, libwebp and, on Linux, OpenSSL (below) linked in. Each package
has the exact configure line (`configure.txt`) and the licence (`COPYING.LGPL`). The libraries
are loaded dynamically, so they can be replaced with another build of the same FFmpeg version.
https://ffmpeg.org/legal.html

## dav1d (BSD 2-Clause), FrameReader.Native.<rid>

dav1d 1.5.4 (https://code.videolan.org/videolan/dav1d), linked into libavcodec as FFmpeg's AV1 decoder.

```
Copyright © 2018-2019, VideoLAN and dav1d authors
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## LAME (GNU LGPL 2 or later), FrameReader.Native.<rid>

LAME 3.100 (https://lame.sourceforge.io), linked into libavcodec as FFmpeg's MP3 encoder (libmp3lame). Built from
https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz (SHA-256 in `configure.txt`); the
libraries can be replaced with another build, as for FFmpeg's. The MP3 patents have expired.
License: https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html

## libwebp (BSD 3-Clause), FrameReader.Native.<rid>

libwebp 1.6.0 (https://chromium.googlesource.com/webm/libwebp), linked into libavcodec as FFmpeg's WebP encoder
(libwebp), with Google's patent grant for WebM's codecs; both are `COPYING.libwebp` in those packages and archives.
Built from https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0.tar.gz (SHA-256 in
`configure.txt`).

```
Copyright (c) 2010, Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## zlib (zlib licence), FrameReader.Native.<rid>

zlib 1.3.2 (https://zlib.net), linked into FFmpeg's libraries. Copyright (C) 1995-2026 Jean-loup Gailly and Mark
Adler. Provided 'as-is', without any express or implied warranty; see https://zlib.net/zlib_license.html.

## OpenSSL (Apache License 2.0), FrameReader.Native.<rid> on Linux

OpenSSL 3.5.9 (https://openssl-library.org), linked into FFmpeg's libraries on Linux for https (Windows and macOS use
the system's TLS). Built from https://github.com/openssl/openssl/releases/download/openssl-3.5.9/openssl-3.5.9.tar.gz
(SHA-256 in `configure.txt`); the licence is `COPYING.openssl` in those packages and archives, and
https://www.apache.org/licenses/LICENSE-2.0.
