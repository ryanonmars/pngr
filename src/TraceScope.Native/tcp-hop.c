// Independent, unprivileged one-shot TCP traceroute probe for Darwin.
// Uses ordinary connect sockets and ICMP datagrams; no BPF, daemon or installation.
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

static double now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1000.0 + t.tv_nsec / 1000000.0;
}
static int number(const char *text, int min, int max) {
    char *end; errno = 0;
    long value = strtol(text, &end, 10);
    return errno || !*text || *end || value < min || value > max ? -1 : (int)value;
}
static int numeric_address(const char *text, int family, struct sockaddr_storage *address) {
    struct addrinfo hints = { .ai_family = family, .ai_socktype = SOCK_STREAM,
        .ai_flags = AI_NUMERICHOST }, *result = NULL;
    if (getaddrinfo(text, NULL, &hints, &result)) return -1;
    if (result->ai_addrlen > sizeof(*address)) { freeaddrinfo(result); return -1; }
    memcpy(address, result->ai_addr, result->ai_addrlen);
    freeaddrinfo(result); return 0;
}
static unsigned char *ip_bytes(struct sockaddr_storage *address) {
    return address->ss_family == AF_INET
        ? (unsigned char *)&((struct sockaddr_in *)address)->sin_addr
        : (unsigned char *)&((struct sockaddr_in6 *)address)->sin6_addr;
}
static unsigned short *port_field(struct sockaddr_storage *address) {
    return address->ss_family == AF_INET
        ? &((struct sockaddr_in *)address)->sin_port : &((struct sockaddr_in6 *)address)->sin6_port;
}
static int reply_matches(const unsigned char *b, ssize_t n, int ipv6,
        struct sockaddr_storage *destination, unsigned short source_port, int *unreachable) {
    int outer = 0;
    if (!ipv6) {
        if (n < 20 || b[0] >> 4 != 4 || b[9] != IPPROTO_ICMP) return 0;
        outer = (b[0] & 15) * 4;
        if (outer < 20) return 0;
    } else if (n >= 40 && b[0] >> 4 == 6) {
        if (b[6] != IPPROTO_ICMPV6) return 0;
        outer = 40;
    }
    if (n < outer + 8) return 0;
    int type = b[outer], code = b[outer + 1];
    int expired = ipv6 ? type == 3 && code == 0 : type == 11 && code == 0;
    *unreachable = ipv6 ? type == 1 : type == 3;
    if (!expired && !*unreachable) return 0;
    const unsigned char *inner = b + outer + 8;
    int remaining = (int)n - outer - 8, header, protocol;
    if (ipv6) {
        if (remaining < 40 || inner[0] >> 4 != 6 || memcmp(inner + 24, ip_bytes(destination), 16)) return 0;
        header = 40; protocol = inner[6];
        for (int i = 0; i < 8 && (protocol == 0 || protocol == 43 || protocol == 44 || protocol == 51 || protocol == 60); i++) {
            if (remaining < header + 2) return 0;
            int next = inner[header];
            int length = protocol == 44 ? 8 : protocol == 51
                ? (inner[header + 1] + 2) * 4 : (inner[header + 1] + 1) * 8;
            if (remaining < header + length) return 0;
            if (protocol == 44 && ((inner[header + 2] << 8 | inner[header + 3]) & 0xfff8)) return 0;
            header += length; protocol = next;
        }
    } else {
        if (remaining < 20 || inner[0] >> 4 != 4 || memcmp(inner + 16, ip_bytes(destination), 4)) return 0;
        if ((inner[6] << 8 | inner[7]) & 0x1fff) return 0;
        header = (inner[0] & 15) * 4; protocol = inner[9];
        if (header < 20) return 0;
    }
    return protocol == IPPROTO_TCP && remaining >= header + 4
        && !memcmp(inner + header, &source_port, 2)
        && !memcmp(inner + header + 2, port_field(destination), 2);
}
int main(int argc, char **argv) {
    if (argc != 7) { fprintf(stderr, "Usage: tcp-hop address ttl port timeout-ms source-or-dash parent-pid\n"); return 2; }
    int ttl = number(argv[2], 1, 64), port = number(argv[3], 1, 65535);
    int timeout = number(argv[4], 100, 30000), parent = number(argv[6], 1, 2147483647);
    struct sockaddr_storage destination = {0}, local = {0};
    if (ttl < 0 || port < 0 || timeout < 0 || parent < 0 || numeric_address(argv[1], AF_UNSPEC, &destination)) return 2;
    if (getppid() != parent) return 3;
    int family = destination.ss_family, ipv6 = family == AF_INET6;
    if (family != AF_INET && family != AF_INET6) return 2;
    *port_field(&destination) = htons((unsigned short)port);
    socklen_t length = ipv6 ? sizeof(struct sockaddr_in6) : sizeof(struct sockaddr_in);
    local.ss_family = family; ((struct sockaddr *)&local)->sa_len = length;
    if (strcmp(argv[5], "-") && numeric_address(argv[5], family, &local)) return 2;
    int icmp = socket(family, SOCK_DGRAM, ipv6 ? IPPROTO_ICMPV6 : IPPROTO_ICMP);
    int tcp = socket(family, SOCK_STREAM, IPPROTO_TCP), result = 1;
    if (icmp < 0 || tcp < 0) { perror("socket"); goto finish; }
    int flags = fcntl(tcp, F_GETFL);
    if (flags < 0 || fcntl(tcp, F_SETFL, flags | O_NONBLOCK) < 0
            || setsockopt(tcp, ipv6 ? IPPROTO_IPV6 : IPPROTO_IP,
                ipv6 ? IPV6_UNICAST_HOPS : IP_TTL, &ttl, sizeof(ttl))
            || bind(tcp, (struct sockaddr *)&local, length)
            || (strcmp(argv[5], "-") && bind(icmp, (struct sockaddr *)&local, length))) { perror("setup"); goto finish; }
    if (getsockname(tcp, (struct sockaddr *)&local, &length)) { perror("getsockname"); goto finish; }
    unsigned short source_port = *port_field(&local);
    double start = now_ms();
    if (connect(tcp, (struct sockaddr *)&destination, length) < 0 && errno != EINPROGRESS) {
        if (errno == ECONNREFUSED || errno == ECONNRESET) {
            printf("%d %s %.3f ms\n", ttl, argv[1], now_ms() - start); result = 0;
        } else perror("connect");
        goto finish;
    }
    struct pollfd fds[2] = {{ .fd = icmp, .events = POLLIN }, { .fd = tcp, .events = POLLOUT }};
    while (now_ms() - start < timeout) {
        // If the desktop crashes or is force-quit, this probe exits within 50 ms.
        if (getppid() != parent) { result = 3; goto finish; }
        int remaining = (int)(timeout - (now_ms() - start));
        if (remaining < 0) break;
        if (poll(fds, 2, remaining > 50 ? 50 : remaining) < 0) {
            if (errno == EINTR) continue;
            perror("poll"); goto finish;
        }
        if (fds[0].revents & POLLIN) {
            unsigned char buffer[4096]; struct sockaddr_storage router;
            socklen_t router_length = sizeof(router);
            ssize_t count = recvfrom(icmp, buffer, sizeof(buffer), 0, (struct sockaddr *)&router, &router_length);
            int unreachable;
            if (count < 0) { if (errno == EINTR || errno == EAGAIN) continue; perror("recvfrom"); goto finish; }
            if (reply_matches(buffer, count, ipv6, &destination, source_port, &unreachable)) {
                char address[INET6_ADDRSTRLEN];
                if (!inet_ntop(family, ip_bytes(&router), address, sizeof(address))) { perror("inet_ntop"); goto finish; }
                printf("%d %s %.3f ms%s\n", ttl, address, now_ms() - start, unreachable ? " !H" : "");
                result = 0; goto finish;
            }
        }
        if (fds[1].revents & (POLLOUT | POLLERR | POLLHUP)) {
            int error = 0; socklen_t error_length = sizeof(error);
            if (getsockopt(tcp, SOL_SOCKET, SO_ERROR, &error, &error_length)) { perror("SO_ERROR"); goto finish; }
            if (error == 0 || error == ECONNREFUSED || error == ECONNRESET) {
                printf("%d %s %.3f ms\n", ttl, argv[1], now_ms() - start); result = 0; goto finish;
            }
            // Socket errors cannot identify routers; wait for matched ICMP.
            fds[1].fd = -1;
        }
    }
    printf("%d *\n", ttl); result = 0;
finish:
    if (tcp >= 0) close(tcp);
    if (icmp >= 0) close(icmp);
    return result;
}
