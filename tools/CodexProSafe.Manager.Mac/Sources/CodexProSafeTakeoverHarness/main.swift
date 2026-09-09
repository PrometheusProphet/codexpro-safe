import Foundation
import ManagerCore

let arguments = Array(CommandLine.arguments.dropFirst())
guard arguments.count == 7, let port = Int(arguments[5]) else {
    FileHandle.standardError.write(Data("usage: harness <inspect|stop> <repository> <workspace> <allowed-root> <node> <port> <profile>\n".utf8))
    exit(64)
}
guard let profile = AccessProfile(rawValue: arguments[6]) else { exit(64) }
let settings = ManagerSettings(repository: arguments[1], workspaceRoot: arguments[2], allowedRoot: arguments[3],
                               nodePath: arguments[4], port: port, accessProfile: profile)
do {
    let plan = try ExternalConnectorInspector.inspect(settings: settings)
    print("owner=\(plan.owner.pid) listener=\(plan.listener.pid)")
    if arguments[0] == "stop" { try await ExternalConnectorInspector.stop(plan: plan, settings: settings) }
    else if arguments[0] != "inspect" { exit(64) }
} catch {
    FileHandle.standardError.write(Data("\(error.localizedDescription)\n".utf8))
    exit(1)
}
