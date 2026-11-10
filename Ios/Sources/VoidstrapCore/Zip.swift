import Foundation
#if canImport(Compression)
import Compression
#endif

public struct ZipError: LocalizedError {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

public struct ZipEntry {
    public let name: String
    let central: Data
    let localOffset: Int
    let compressedSize: Int
    public let uncompressedSize: Int
    let method: Int

    public var isDirectory: Bool { name.hasSuffix("/") }
}

public final class ZipReader {
    let handle: FileHandle
    public let entries: [ZipEntry]
    let centralOffset: Int

    public init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
        do {
            let size = Int(try handle.seekToEnd())
            guard size >= 22 else { throw ZipError("This file is not a zip archive.") }
            let tailCount = min(size, 22 + 0xFFFF)
            let tail = try Self.read(handle, size - tailCount, tailCount)
            var eocd = tail.count - 22
            while eocd >= 0, tail.le32(eocd) != 0x0605_4B50 { eocd -= 1 }
            guard eocd >= 0 else { throw ZipError("This file is not a zip archive.") }
            let count = tail.le16(eocd + 10)
            let cdSize = tail.le32(eocd + 12)
            let cdOffset = tail.le32(eocd + 16)
            guard count != 0xFFFF, cdOffset != 0xFFFF_FFFF else { throw ZipError("Zip64 archives are not supported.") }
            guard cdOffset + cdSize <= size else { throw ZipError("The zip archive is damaged.") }
            centralOffset = cdOffset
            let cd = try Self.read(handle, cdOffset, cdSize)
            var list: [ZipEntry] = []
            list.reserveCapacity(count)
            var p = 0
            for _ in 0..<count {
                guard p + 46 <= cd.count, cd.le32(p) == 0x0201_4B50 else { throw ZipError("The zip archive is damaged.") }
                let nameLen = cd.le16(p + 28)
                let length = 46 + nameLen + cd.le16(p + 30) + cd.le16(p + 32)
                guard p + length <= cd.count else { throw ZipError("The zip archive is damaged.") }
                let name = String(decoding: cd.subdata(in: p + 46..<p + 46 + nameLen), as: UTF8.self)
                list.append(ZipEntry(name: name, central: cd.subdata(in: p..<p + length), localOffset: cd.le32(p + 42),
                                     compressedSize: cd.le32(p + 20), uncompressedSize: cd.le32(p + 24), method: cd.le16(p + 10)))
                p += length
            }
            entries = list
        } catch {
            try? handle.close()
            throw error
        }
    }

    deinit { try? handle.close() }

    public func entry(named name: String) -> ZipEntry? {
        entries.first { $0.name == name }
    }

    public func data(of entry: ZipEntry) throws -> Data {
        let header = try Self.read(handle, entry.localOffset, 30)
        guard header.le32(0) == 0x0403_4B50 else { throw ZipError("The zip archive is damaged.") }
        let raw = try Self.read(handle, entry.localOffset + 30 + header.le16(26) + header.le16(28), entry.compressedSize)
        switch entry.method {
        case 0: return raw
        case 8: return try inflateRaw(raw, size: entry.uncompressedSize)
        default: throw ZipError("Unsupported compression in \(entry.name).")
        }
    }

    func copy(from offset: Int, count: Int, to out: FileHandle) throws {
        try handle.seek(toOffset: UInt64(offset))
        var left = count
        while left > 0 {
            let chunk = try autoreleasepool { try handle.read(upToCount: min(left, 1 << 20)) ?? Data() }
            guard !chunk.isEmpty else { throw ZipError("The zip archive ended early.") }
            try out.write(contentsOf: chunk)
            left -= chunk.count
        }
    }

    static func read(_ handle: FileHandle, _ offset: Int, _ count: Int) throws -> Data {
        guard count > 0 else { return Data() }
        try handle.seek(toOffset: UInt64(offset))
        let data = try handle.read(upToCount: count) ?? Data()
        guard data.count == count else { throw ZipError("The zip archive ended early.") }
        return data
    }
}

#if !canImport(ObjectiveC)
func autoreleasepool<T>(invoking body: () throws -> T) rethrows -> T { try body() }
#endif

extension Data {
    func le16(_ i: Int) -> Int { Int(self[startIndex + i]) | Int(self[startIndex + i + 1]) << 8 }
    func le32(_ i: Int) -> Int { le16(i) | le16(i + 2) << 16 }
}

func le16(_ v: Int) -> Data { Data([UInt8(v & 0xFF), UInt8(v >> 8 & 0xFF)]) }
func le32(_ v: Int) -> Data { le16(v & 0xFFFF) + le16(v >> 16 & 0xFFFF) }

private let crcTable: [UInt32] = (0..<256).map { n -> UInt32 in
    var c = UInt32(n)
    for _ in 0..<8 { c = c & 1 != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1 }
    return c
}

func crc32(_ data: Data) -> Int {
    var c: UInt32 = 0xFFFF_FFFF
    data.withUnsafeBytes { raw in
        for b in raw { c = crcTable[Int((c ^ UInt32(b)) & 0xFF)] ^ (c >> 8) }
    }
    return Int(c ^ 0xFFFF_FFFF)
}

func inflateRaw(_ data: Data, size: Int) throws -> Data {
    if size == 0 { return Data() }
    #if canImport(Compression)
    var out = Data(count: size)
    let n = out.withUnsafeMutableBytes { dst in
        data.withUnsafeBytes { src in
            compression_decode_buffer(dst.bindMemory(to: UInt8.self).baseAddress!, size,
                                      src.bindMemory(to: UInt8.self).baseAddress!, data.count, nil, COMPRESSION_ZLIB)
        }
    }
    guard n == size else { throw ZipError("A file in the zip archive could not be read.") }
    return out
    #else
    throw ZipError("Compressed archives need iOS.")
    #endif
}
