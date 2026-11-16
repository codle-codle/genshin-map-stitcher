Components statically linked into OpenCvSharpExtern.dll
(NuGet package OpenCvSharp5.runtime.win 5.0.0.20261018, Windows x64).

The list was built from the OpenCvSharp 5.0.0.20261018 build files (vcpkg.json with its vcpkg baseline,
cmake/opencv_build_options.cmake), the OpenCV 5.0.0 and opencv_contrib 5.0.0 source trees, and text markers
found inside the DLL. When in doubt a component was included. Each folder holds the original license text.

Folder | Component | Version | License | Source
opencv | OpenCV | 5.0.0 | Apache-2.0 | https://github.com/opencv/opencv
opencv_contrib | OpenCV contrib modules | 5.0.0 | Apache-2.0 | https://github.com/opencv/opencv_contrib
softfloat | Berkeley SoftFloat (in OpenCV core) | OpenCV 5.0.0 | BSD-3-Clause | https://github.com/opencv/opencv/tree/5.0.0/modules/core/3rdparty/SoftFloat
annoy | Annoy (in OpenCV features) | OpenCV 5.0.0 | Apache-2.0 | https://github.com/opencv/opencv/tree/5.0.0/modules/features/3rdparty/annoy
opencl-headers | OpenCL headers (in OpenCV) | OpenCV 5.0.0 | Apache-2.0 / MIT-style | https://github.com/opencv/opencv/tree/5.0.0/3rdparty/include/opencl
protobuf | Protocol Buffers (OpenCV 3rdparty) | OpenCV 5.0.0 | BSD-3-Clause | https://github.com/opencv/opencv/tree/5.0.0/3rdparty/protobuf
flatbuffers | FlatBuffers (OpenCV 3rdparty) | OpenCV 5.0.0 | Apache-2.0 | https://github.com/opencv/opencv/tree/5.0.0/3rdparty/flatbuffers
dlpack | DLPack (OpenCV 3rdparty) | OpenCV 5.0.0 | Apache-2.0 | https://github.com/opencv/opencv/tree/5.0.0/3rdparty/dlpack
mlas | MLAS (OpenCV 3rdparty) | OpenCV 5.0.0 | MIT | https://github.com/opencv/opencv/tree/5.0.0/3rdparty/mlas
ittnotify | Intel ITT API (OpenCV 3rdparty) | OpenCV 5.0.0 | BSD-3-Clause OR GPL-2.0 | https://github.com/opencv/opencv/tree/5.0.0/3rdparty/ittnotify
ippicv | Intel IPP ICV (downloaded by OpenCV build) | 2026.0.0 win intel64 | Intel Simplified Software License | https://github.com/opencv/opencv_3rdparty/tree/406d398c436d0465c8e53dd432d9ecd9301d5f4a/ippicv
tesseract | Tesseract OCR | 5.5.2 | Apache-2.0 | https://github.com/tesseract-ocr/tesseract
leptonica | Leptonica | 1.87.0 | BSD-2-Clause | https://github.com/DanBloomberg/leptonica
libarchive | libarchive | 3.8.5 | BSD-2-Clause and others | https://github.com/libarchive/libarchive
curl | curl | 8.18.0 | curl | https://github.com/curl/curl
openssl | OpenSSL | 3.6.1 | Apache-2.0 | https://github.com/openssl/openssl
zlib | zlib | 1.3.1 | Zlib | https://github.com/madler/zlib
libpng | libpng | 1.6.55 | libpng-2.0 | https://github.com/pnggroup/libpng
libjpeg-turbo | libjpeg-turbo | 3.1.3 | IJG AND BSD-3-Clause AND Zlib | https://github.com/libjpeg-turbo/libjpeg-turbo
libtiff | LibTIFF | 4.7.1 | libtiff | https://gitlab.com/libtiff/libtiff
libwebp | libwebp | 1.6.0 | BSD-3-Clause | https://github.com/webmproject/libwebp
openjpeg | OpenJPEG | 2.5.4 | BSD-2-Clause | https://github.com/uclouvain/openjpeg
giflib | GIFLIB | 5.2.2 | MIT | https://sourceforge.net/projects/giflib/
lz4 | LZ4 | 1.10.0 | BSD-2-Clause | https://github.com/lz4/lz4
xz | XZ Utils (liblzma) | 5.8.2 | 0BSD | https://github.com/tukaani-project/xz
bzip2 | bzip2 | 1.0.8 | bzip2-1.0.6 | https://sourceware.org/bzip2/
zstd | Zstandard | 1.5.7 | BSD-3-Clause (chosen of BSD-3-Clause OR GPL-2.0) | https://github.com/facebook/zstd
