#!/usr/bin/env bash
set -euo pipefail
output=/opt/mtm-monitoring/textfile/docker.prom
temporary="${output}.tmp"
{
  docker ps -a --format '{{.Names}}|{{.Status}}' | while IFS='|' read -r name status; do
    active=0; [[ "$status" == Up* ]] && active=1
    printf 'rn_service_active{name="%s"} %s\n' "$name" "$active"
  done
  docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' | while IFS='|' read -r name cpu memory; do
    cpu=${cpu%%%}; used=${memory%% / *}
    case "$used" in
      *GiB) bytes=$(awk -v n="${used%GiB}" 'BEGIN {printf "%.0f",n*1073741824}') ;;
      *MiB) bytes=$(awk -v n="${used%MiB}" 'BEGIN {printf "%.0f",n*1048576}') ;;
      *KiB) bytes=$(awk -v n="${used%KiB}" 'BEGIN {printf "%.0f",n*1024}') ;;
      *B) bytes=${used%B} ;;
      *) bytes=0 ;;
    esac
    printf 'rn_container_cpu_percent{name="%s"} %s\n' "$name" "${cpu:-0}"
    printf 'rn_container_memory_bytes{name="%s"} %s\n' "$name" "${bytes:-0}"
  done
} > "$temporary"
mv "$temporary" "$output"
