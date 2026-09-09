#ifndef PROCESS_INSPECTION_C_H
#define PROCESS_INSPECTION_C_H

#include <stdint.h>
#include <sys/types.h>

typedef struct {
    pid_t pid;
    pid_t parent_pid;
    pid_t process_group_id;
    uint64_t start_time_microseconds;
} CPSProcessInfo;

int cps_process_info(pid_t pid, CPSProcessInfo *result);
int cps_process_path(pid_t pid, char *buffer, uint32_t capacity);
int cps_process_cwd(pid_t pid, char *buffer, uint32_t capacity);
int cps_process_arguments(pid_t pid, void *buffer, size_t *length);
int cps_process_group_members(pid_t process_group_id, pid_t *buffer, int capacity);

#endif
