/* A native Universal 2 app entry point. Each .NET runtime remains architecture-specific. */
#include <mach-o/dyld.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    (void)argc;
    char executable[PATH_MAX], resolved[PATH_MAX], payload[PATH_MAX];
    uint32_t size = sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0 || !realpath(executable, resolved)) {
        perror("pngr: locate application");
        return 1;
    }
    char *separator = strrchr(resolved, '/');
    if (!separator) return 1;
    *separator = '\0';
#if defined(__arm64__)
    const char *runtime = "osx-arm64";
#elif defined(__x86_64__)
    const char *runtime = "osx-x64";
#else
#error Unsupported macOS architecture
#endif
    int length = snprintf(payload, sizeof(payload), "%s/../Resources/runtimes/%s/pngr", resolved, runtime);
    if (length < 0 || length >= (int)sizeof(payload)) return 1;
    argv[0] = payload;
    execv(payload, argv);
    perror("pngr: launch native runtime");
    return 1;
}
