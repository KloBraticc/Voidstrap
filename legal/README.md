# License notices

`components.json` records the package versions, publisher terms, document origins and bundled asset notices. Files in `texts` retain the original license text. Their filenames contain the first 20 characters of their SHA256 checksum.

When updating a dependency, update the inventory and THIRD-PARTY-NOTICES.txt together. Review direct and transitive dependencies, native components, document checksums and bundled source archives. NuGet package metadata, Maven POMs and the published package contents are the sources for the inventory. Preserve publisher notices rather than assigning every dependency the project's MIT license.

`THIRD-PARTY-NOTICES.txt` is embedded in the Windows and cross platform app. Windows compresses the embedded notices and reads them when the licenses page opens. It embeds the Bibata source archive too and does not create a separate licenses folder beside the executable. Linux and macOS packages carry the notices and source archive as files. Android includes the notices as an asset and shows them from its licenses screen. The Bibata source archive carries its editable SVG source and build files with the GPL cursor images.

`sources/Bibata-Cursor-v2.0.7.tar.gz` contains the complete tracked source tree at upstream commit `35ccfe209a808e40d6c2ca60a46cbe4faf68b690`. It includes the SVG artwork, rendering settings, build scripts, dependency lockfile and GPL license. The archive excludes Git metadata and normalizes timestamps and archive ownership. Voidstrap's adapted PNG files remain in the desktop resources and Android assets. These third party cursor assets keep GPLv3 terms. This does not relabel the original Voidstrap code as GPL.

ImageSharp 3.1.12 grants Apache 2.0 terms to this open source project under the Six Labors Split License. The original split-license text is retained so its conditions remain visible. The Play app update libraries and Google Play services have separate publisher terms, linked in their inventory entries. They are not MIT dependencies.

## Verification limits

The Windows SDK reference and documentation packages used by the compiler are not inventoried as shipped runtime libraries. Keep their SDK terms when distributing those tools themselves.

The Android desugaring library `com.android.tools:desugar_jdk_libs_nio:2.1.5` is GPLv2 with the Classpath Exception. Its corresponding source is included in `sources/desugar_jdk_libs_nio-2.1.5.tar.xz` and bundled in Android's `assets/licenses` directory alongside these notices. The archive contains the complete upstream source tree, original licenses, Bazel build files, version files and dependency definitions at commit `73170c345e6a762fc6a1f0301bb15218850023ef`, plus the publisher's dependency provenance.

R8 tag `8.10.21` pins dependency archive `e4c206c7bdfe1415cd2716a1d59d699a80bc63dd`, whose `README.google` and `desugar_jdk_libs_hash` identify that source commit. All 1,461 class entries and every other ZIP entry in its library JAR match the publisher's raw 2.1.5 release by content. ZIP container metadata differs. The source's `VERSION_JDK11_NIO.txt` and `DEPENDENCIES_JDK11_NIO.txt` both identify 2.1.5. The JAR inside the publisher's Maven release ZIP matches Google Maven and the resolved Gradle dependency exactly, with SHA256 `d8044befae095781b9a80bf1faa92edc30382d75d437476784c1bf991598a976`. The source archive's SHA256 is `97bd9e0d92e9227fe944d669547155fab741c28cf95bc5b82f4b534948395286`. This verifies release provenance and includes the corresponding source; it does not claim a reproducible rebuild of the publisher's toolchain.

The inventory does not establish ownership of every project image, sound, mod or copied code fragment. Assets without a documented origin still need provenance review. Build and package checks establish that notices are carried in the tested output, not that an existing published release already contains them.
