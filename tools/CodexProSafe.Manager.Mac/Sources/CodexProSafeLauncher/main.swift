import Foundation
import Darwin

let arguments = Array(CommandLine.arguments.dropFirst())
guard let executable = arguments.first, executable.hasPrefix("/"),
      FileManager.default.isExecutableFile(atPath: executable) else {
    FileHandle.standardError.write(Data("CodexProSafeLauncher requires one absolute executable path.\n".utf8))
    exit(64)
}
guard setpgid(0, 0) == 0 else {
    FileHandle.standardError.write(Data("CodexProSafeLauncher could not create its process group.\n".utf8))
    exit(70)
}

let childArguments = arguments
let pointers = childArguments.map { strdup($0) } + [nil]
defer { for pointer in pointers { if let pointer { free(pointer) } } }
var mutablePointers = pointers
execv(executable, &mutablePointers)
FileHandle.standardError.write(Data("CodexProSafeLauncher could not execute the configured binary.\n".utf8))
exit(71)
