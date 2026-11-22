import Foundation
import SystemConfiguration
import CoreWLAN

func SCDynamicStoreCopyLocalHostNameSafe() -> String? {
    SCDynamicStoreCopyLocalHostName(nil) as String?
}

enum LinkKind { case wifi, ethernet }

struct InterfaceInfo: Equatable {
    let bsdName: String        // en0
    let displayName: String    // Wi-Fi, Belkin USB-C LAN
    let kind: LinkKind
    let isUp: Bool             // has an IPv4 address and the link is running
    let ip: String?            // IPv4
    let mac: String            // 7C:F3:4D:EB:54:9A
    let speed: String?         // "1 Gb/s", nil when unknown
}

enum NetworkInfo {
    /// Physical Wi-Fi and Ethernet ports worth offering in the menu, wired first.
    static func candidates() -> [InterfaceInfo] {
        let addrs = readIfaddrs()
        let baud = readBaudRates()
        var out: [InterfaceInfo] = []
        var seen = Set<String>()

        for iface in (SCNetworkInterfaceCopyAll() as? [SCNetworkInterface]) ?? [] {
            guard let bsd = SCNetworkInterfaceGetBSDName(iface) as String?, !seen.contains(bsd),
                  let type = SCNetworkInterfaceGetInterfaceType(iface) as String? else { continue }
            let name = (SCNetworkInterfaceGetLocalizedDisplayName(iface) as String?) ?? bsd
            let kind: LinkKind
            if type == (kSCNetworkInterfaceTypeIEEE80211 as String) { kind = .wifi }
            else if type == (kSCNetworkInterfaceTypeEthernet as String) { kind = .ethernet }
            else { continue } // Bridge, Bluetooth, PPP, VPN, …
            if isExcluded(bsd: bsd, name: name) { continue }

            let a = addrs[bsd]
            let ip = a?.ipv4
            let macBytes = a?.mac ?? []
            // Locally administered MAC on a wired port that has no address = an internal/virtual NIC
            // (Apple Silicon has a few "Ethernet Adapter (enX)" ones). Wi-Fi private addresses are fine.
            if kind == .ethernet, ip == nil, let first = macBytes.first, first & 0x02 != 0 { continue }

            let up = ip != nil && (a?.running ?? false)
            var speed: String?
            if up {
                switch kind {
                case .wifi:
                    if let rate = CWWiFiClient.shared().interface(withName: bsd)?.transmitRate(), rate > 0 {
                        speed = formatSpeed(rate * 1_000_000)
                    }
                case .ethernet:
                    if let b = baud[bsd], b > 0 { speed = formatSpeed(Double(b)) }
                    else if let b = mediaSpeed(bsd) { speed = formatSpeed(b) }
                }
            }
            seen.insert(bsd)
            out.append(InterfaceInfo(bsdName: bsd, displayName: name, kind: kind, isUp: up, ip: ip,
                                     mac: macBytes.isEmpty ? "-" : macBytes.map { String(format: "%02X", $0) }.joined(separator: ":"),
                                     speed: speed))
        }
        return out.sorted {
            if $0.kind != $1.kind { return $0.kind == .ethernet }
            return $0.bsdName.compare($1.bsdName, options: .numeric) == .orderedAscending
        }
    }

    /// Thunderbolt Bridge members and other things that are never "the network".
    private static func isExcluded(bsd: String, name: String) -> Bool {
        let prefixes = ["utun", "bridge", "awdl", "llw", "lo", "gif", "stf", "ap", "anpi", "vmenet", "vnic", "ipsec", "ppp"]
        if prefixes.contains(where: { bsd.hasPrefix($0) }) { return true }
        // "Thunderbolt 1", "Thunderbolt 2" … are the Thunderbolt Bridge ports (but keep "Thunderbolt Ethernet …").
        if name.range(of: #"^Thunderbolt( Bridge| \d+)$"#, options: .regularExpression) != nil { return true }
        return false
    }

    // MARK: - getifaddrs

    private struct Addr { var ipv4: String?; var mac: [UInt8] = []; var running = false }

    private static func readIfaddrs() -> [String: Addr] {
        var result: [String: Addr] = [:]
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0 else { return result }
        defer { freeifaddrs(head) }
        var p = head
        while let a = p {
            defer { p = a.pointee.ifa_next }
            guard let sa = a.pointee.ifa_addr else { continue }
            let name = String(cString: a.pointee.ifa_name)
            var entry = result[name] ?? Addr()
            let flags = Int32(a.pointee.ifa_flags)
            entry.running = entry.running || (flags & IFF_UP != 0 && flags & IFF_RUNNING != 0)
            switch Int32(sa.pointee.sa_family) {
            case AF_INET:
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                    let ip = String(cString: host)
                    // Prefer a real address over a 169.254.x.x self-assigned one.
                    if entry.ipv4 == nil || (entry.ipv4!.hasPrefix("169.254.") && !ip.hasPrefix("169.254.")) {
                        entry.ipv4 = ip
                    }
                }
            case AF_LINK:
                sa.withMemoryRebound(to: sockaddr_dl.self, capacity: 1) { dl in
                    let len = Int(dl.pointee.sdl_alen)
                    guard len == 6 else { return }
                    let offset = Int(dl.pointee.sdl_nlen)
                    // sdl_data is a short fixed array the name+address can run past, so read from its base pointer.
                    let base = UnsafeRawPointer(dl).advanced(by: MemoryLayout<sockaddr_dl>.offset(of: \sockaddr_dl.sdl_data)!)
                    entry.mac = (0..<len).map { base.load(fromByteOffset: offset + $0, as: UInt8.self) }
                }
            default: break
            }
            result[name] = entry
        }
        return result
    }

    // MARK: - link speed

    /// 64-bit link speeds from NET_RT_IFLIST2 (getifaddrs' if_data is 32-bit and tops out at 4.29 Gb/s).
    private static func readBaudRates() -> [String: UInt64] {
        var mib: [Int32] = [CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, 0]
        var len = 0
        guard sysctl(&mib, 6, nil, &len, nil, 0) == 0, len > 0 else { return [:] }
        var buf = [UInt8](repeating: 0, count: len)
        guard sysctl(&mib, 6, &buf, &len, nil, 0) == 0 else { return [:] }
        var out: [String: UInt64] = [:]
        buf.withUnsafeBytes { raw in
            var off = 0
            while off + MemoryLayout<if_msghdr>.size <= len {
                let base = raw.baseAddress!.advanced(by: off)
                let hdr = base.loadUnaligned(as: if_msghdr.self)
                let msglen = Int(hdr.ifm_msglen)
                if msglen == 0 { break }
                if Int32(hdr.ifm_type) == RTM_IFINFO2, off + MemoryLayout<if_msghdr2>.size <= len {
                    let h2 = base.loadUnaligned(as: if_msghdr2.self)
                    var name = [CChar](repeating: 0, count: Int(IF_NAMESIZE))
                    if if_indextoname(UInt32(h2.ifm_index), &name) != nil {
                        out[String(cString: name)] = h2.ifm_data.ifi_baudrate
                    }
                }
                off += msglen
            }
        }
        return out
    }

    /// Fallback: parse "media: autoselect (2500Base-T <full-duplex>)" from ifconfig.
    private static func mediaSpeed(_ bsd: String) -> Double? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/sbin/ifconfig")
        p.arguments = [bsd]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard let text = String(data: data, encoding: .utf8),
              let m = text.range(of: #"\((\d+)(G?)[Bb]ase"#, options: .regularExpression) else { return nil }
        let token = String(text[m]).dropFirst()
        let isG = token.contains("G")
        guard let n = Double(token.prefix { $0.isNumber }) else { return nil }
        return isG ? n * 1e9 : n * 1e6
    }

    static func formatSpeed(_ bps: Double) -> String {
        if bps >= 1e9 {
            let g = bps / 1e9
            return g.rounded() == g || g >= 10 ? String(format: "%.0f Gb/s", g) : String(format: "%.1f Gb/s", g)
        }
        if bps >= 1e6 { return String(format: "%.0f Mb/s", bps / 1e6) }
        return String(format: "%.0f Kb/s", bps / 1e3)
    }
}

/// Calls back (on the main queue) whenever an interface's IPv4 or link state changes.
final class NetworkWatcher {
    private var store: SCDynamicStore?
    private let onChange: () -> Void

    init(onChange: @escaping () -> Void) {
        self.onChange = onChange
        var ctx = SCDynamicStoreContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(),
                                        retain: nil, release: nil, copyDescription: nil)
        store = SCDynamicStoreCreate(nil, "IPWidget" as CFString, { _, _, info in
            guard let info else { return }
            let me = Unmanaged<NetworkWatcher>.fromOpaque(info).takeUnretainedValue()
            DispatchQueue.main.async { me.onChange() }
        }, &ctx)
        guard let store else { return }
        let patterns = ["State:/Network/Interface/.*/IPv4", "State:/Network/Interface/.*/Link",
                        "State:/Network/Interface/.*/AirPort", "State:/Network/Global/IPv4"] as CFArray
        SCDynamicStoreSetNotificationKeys(store, nil, patterns)
        SCDynamicStoreSetDispatchQueue(store, .main)
    }

    deinit {
        if let store { SCDynamicStoreSetDispatchQueue(store, nil) }
    }
}
