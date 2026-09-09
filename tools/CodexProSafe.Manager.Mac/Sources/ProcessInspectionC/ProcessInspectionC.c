#include "ProcessInspectionC.h"

#include <errno.h>
#include <libproc.h>
#include <stdlib.h>
#include <string.h>
#include <sys/sysctl.h>
#include <unistd.h>

int cps_process_info(pid_t pid, CPSProcessInfo *result) {
    if (result == NULL) return EINVAL;
    struct proc_bsdinfo info = {0};
    int bytes = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info));
    if (bytes != sizeof(info)) return errno == 0 ? ESRCH : errno;
    result->pid = pid;
    result->parent_pid = (pid_t)info.pbi_ppid;
    result->process_group_id = (pid_t)info.pbi_pgid;
    result->start_time_microseconds = ((uint64_t)info.pbi_start_tvsec * 1000000ULL) + info.pbi_start_tvusec;
    return 0;
}

int cps_process_path(pid_t pid, char *buffer, uint32_t capacity) {
    if (buffer == NULL || capacity == 0) return EINVAL;
    int bytes = proc_pidpath(pid, buffer, capacity);
    return bytes > 0 ? 0 : (errno == 0 ? ESRCH : errno);
}

int cps_process_cwd(pid_t pid, char *buffer, uint32_t capacity) {
    if (buffer == NULL || capacity == 0) return EINVAL;
    struct proc_vnodepathinfo info = {0};
    int bytes = proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, &info, sizeof(info));
    if (bytes != sizeof(info)) return errno == 0 ? ESRCH : errno;
    size_t length = strnlen(info.pvi_cdir.vip_path, sizeof(info.pvi_cdir.vip_path));
    if (length + 1 > capacity) return ERANGE;
    memcpy(buffer, info.pvi_cdir.vip_path, length);
    buffer[length] = '\0';
    return 0;
}

int cps_process_arguments(pid_t pid, void *buffer, size_t *length) {
    if (buffer == NULL || length == NULL || *length == 0) return EINVAL;
    int query[] = { CTL_KERN, KERN_PROCARGS2, pid };
    if (sysctl(query, 3, buffer, length, NULL, 0) != 0) return errno;
    return 0;
}

int cps_process_group_members(pid_t process_group_id, pid_t *buffer, int capacity) {
    if (buffer == NULL || capacity <= 0) return -EINVAL;
    int count = proc_listallpids(NULL, 0);
    if (count <= 0) return -(errno == 0 ? ESRCH : errno);
    int all_capacity = count + 1024;
    pid_t *all = calloc((size_t)all_capacity, sizeof(pid_t));
    if (all == NULL) return -ENOMEM;
    int listed = proc_listallpids(all, all_capacity * (int)sizeof(pid_t));
    if (listed < 0) {
        int failure = errno == 0 ? ESRCH : errno;
        free(all);
        return -failure;
    }
    if (listed >= all_capacity) {
        free(all);
        return -ERANGE;
    }
    int matched = 0;
    for (int index = 0; index < listed; index++) {
        if (getpgid(all[index]) != process_group_id) continue;
        if (matched >= capacity) {
            free(all);
            return -ERANGE;
        }
        buffer[matched++] = all[index];
    }
    free(all);
    return matched;
}
