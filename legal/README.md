# License notices

`components.json` records the package versions, publisher terms, document origins and bundled asset notices. Files in `texts` retain the original license text. Their filenames contain the first 20 characters of their SHA256 checksum.

When updating a dependency, update the inventory and THIRD-PARTY-NOTICES.txt together. Review direct and transitive dependencies, native components, document checksums and bundled source archives. NuGet package metadata, Maven POMs and the published package contents are the sources for the inventory. Preserve publisher notices rather than assigning every dependency the project's MIT license.

The generated `THIRD-PARTY-NOTICES.txt` is embedded in the Windows and cross platform app and copied into desktop release output. Android includes it as an asset and shows it from its licenses screen. The Bibata source archive accompanies packages so that its editable SVG source and build files remain available with the GPL cursor images.

`sources/Bibata-Cursor-v2.0.7.tar.gz` contains the complete tracked source tree at upstream commit `35ccfe209a808e40d6c2ca60a46cbe4faf68b690`. It includes the SVG artwork, rendering settings, build scripts, dependency lockfile and GPL license. The archive excludes Git metadata and normalizes timestamps and archive ownership. Voidstrap's adapted PNG files remain in the desktop resources and Android assets. These third party cursor assets keep GPLv3 terms. This does not relabel the original Voidstrap code as GPL.

ImageSharp 3.1.12 grants Apache 2.0 terms to this open source project under the Six Labors Split License. The original split-license text is retained so its conditions remain visible. The Play app update libraries and Google Play services have separate publisher terms, linked in their inventory entries. They are not MIT dependencies.

## Verification limits

The Windows SDK reference and documentation packages used by the compiler are not inventoried as shipped runtime libraries. Keep their SDK terms when distributing those tools themselves.

The Android desugaring library `com.android.tools:desugar_jdk_libs_nio:2.1.5` is GPLv2 with the Classpath Exception. Its license is included, but the exact corresponding source release has not been verified or archived here. The publisher's R8 source and release servers were inaccessible during this audit, and Google Maven did not provide a sources JAR at the checked artifact locations. Before redistributing an Android release, verify and provide its corresponding source under the publisher's terms. Including its license text alone does not resolve that source requirement.

The inventory does not establish ownership of every project image, sound, mod or copied code fragment. Assets without a documented origin still need provenance review. Build and package checks establish that notices are carried in the tested output, not that an existing published release already contains them.
