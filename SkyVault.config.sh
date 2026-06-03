#!/usr/bin/env sh
set -eu

cd "$(dirname "$0")"

env_file=".env"
use_tui=0

if [ -t 0 ] && command -v whiptail >/dev/null 2>&1; then
  use_tui=1
fi

get_env_value() {
  key="$1"
  fallback="$2"

  if [ ! -f "$env_file" ]; then
    printf '%s' "$fallback"
    return
  fi

  line="$(grep -E "^${key}=" "$env_file" | tail -n 1 || true)"

  if [ -z "$line" ]; then
    printf '%s' "$fallback"
    return
  fi

  value="${line#*=}"
  value="${value#\'}"
  value="${value%\'}"
  printf '%s' "$value"
}

quote_env() {
  printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"
}

write_env_value() {
  key="$1"
  value="$2"
  quoted="$(quote_env "$value")"

  if [ -f "$env_file" ] && grep -q -E "^${key}=" "$env_file"; then
    tmp="$(mktemp)"
    while IFS= read -r line || [ -n "$line" ]; do
      case "$line" in
        "$key"=*) printf '%s=%s\n' "$key" "$quoted" ;;
        *) printf '%s\n' "$line" ;;
      esac
    done < "$env_file" > "$tmp"
    mv "$tmp" "$env_file"
  else
    printf '%s=%s\n' "$key" "$quoted" >> "$env_file"
  fi
}

pause() {
  if [ "$use_tui" -eq 1 ]; then
    whiptail --title "SkyVault" --msgbox "Enter aby kontynuowac." 8 44
    return
  fi

  printf '\nEnter aby kontynuowac... '
  IFS= read -r _
}

ask() {
  label="$1"
  current="$2"

  if [ "$use_tui" -eq 1 ]; then
    value="$(whiptail --title "SkyVault config" --inputbox "$label" 9 70 "$current" 3>&1 1>&2 2>&3)" || return 1

    if [ -z "$value" ]; then
      value="$current"
    fi

    printf '%s' "$value"
    return
  fi

  printf '%s [%s]: ' "$label" "$current" >&2
  IFS= read -r value

  if [ -z "$value" ]; then
    value="$current"
  fi

  printf '%s' "$value"
}

ask_secret() {
  label="$1"
  current="$2"
  shown="empty"

  if [ -n "$current" ]; then
    shown="set"
  fi

  if [ "$use_tui" -eq 1 ]; then
    value="$(whiptail --title "SkyVault config" --passwordbox "$label ($shown)" 9 70 3>&1 1>&2 2>&3)" || return 1

    if [ -z "$value" ]; then
      value="$current"
    fi

    printf '%s' "$value"
    return
  fi

  printf '%s [%s]: ' "$label" "$shown" >&2

  if [ -t 0 ]; then
    stty -echo
    IFS= read -r value
    stty echo
    printf '\n' >&2
  else
    IFS= read -r value
  fi

  if [ -z "$value" ]; then
    value="$current"
  fi

  printf '%s' "$value"
}

require_number() {
  label="$1"
  value="$2"

  case "$value" in
    ''|*[!0-9]*)
      printf 'Blad: %s musi byc liczba.\n' "$label" >&2
      return 1
      ;;
  esac
}

require_bool() {
  label="$1"
  value="$2"

  case "$value" in
    true|false|TRUE|FALSE) return 0 ;;
    *)
      printf 'Blad: %s musi byc true albo false.\n' "$label" >&2
      return 1
      ;;
  esac
}

require_storage_mode() {
  value="$1"

  case "$value" in
    single|spread|mirror|raid0|raid1) return 0 ;;
    *)
      printf 'Blad: tryb storage musi byc single, spread, mirror, raid0 albo raid1.\n' >&2
      return 1
      ;;
  esac
}

menu_select() {
  title="$1"
  text="$2"
  height="$3"
  width="$4"
  menu_height="$5"
  shift 5

  if [ "$use_tui" -eq 1 ]; then
    whiptail --title "$title" --menu "$text" "$height" "$width" "$menu_height" "$@" 3>&1 1>&2 2>&3
    return
  fi

  printf '\n%s\n' "$title" >&2
  printf '%s\n' "$text" >&2

  while [ "$#" -gt 0 ]; do
    printf '%s. %s\n' "$1" "$2" >&2
    shift 2
  done

  printf 'Wybor: ' >&2
  IFS= read -r choice
  printf '%s' "$choice"
}

info_box() {
  title="$1"
  text="$2"

  if [ "$use_tui" -eq 1 ]; then
    whiptail --title "$title" --msgbox "$text" 12 70
    return
  fi

  printf '\n%s\n%s\n' "$title" "$text"
  printf '\nEnter aby kontynuowac... ' >&2
  IFS= read -r _
}

set_value() {
  key="$1"
  label="$2"
  fallback="$3"
  validator="${4:-}"

  value="$(ask "$label" "$(get_env_value "$key" "$fallback")")" || return

  case "$validator" in
    number) require_number "$label" "$value" || return ;;
    bool) require_bool "$label" "$value" || return ;;
    storage_mode) require_storage_mode "$value" || return ;;
  esac

  write_env_value "$key" "$value"
  info_box "SkyVault" "Zapisano: $label = $value"
}

set_secret_value() {
  key="$1"
  label="$2"
  fallback="$3"

  value="$(ask_secret "$label" "$(get_env_value "$key" "$fallback")")" || return
  write_env_value "$key" "$value"
  info_box "SkyVault" "Zapisano: $label"
}

set_default_config() {
  write_env_value SMTP_HOST "$(get_env_value SMTP_HOST smtp.gmail.com)"
  write_env_value SMTP_PORT "$(get_env_value SMTP_PORT 587)"
  write_env_value SMTP_USER "$(get_env_value SMTP_USER skyvaultcloud0@gmail.com)"
  write_env_value SMTP_PASS "$(get_env_value SMTP_PASS your-gmail-app-password)"
  write_env_value SMTP_FROM "$(get_env_value SMTP_FROM skyvaultcloud0@gmail.com)"
  write_env_value SMTP_SSL "$(get_env_value SMTP_SSL true)"
  write_env_value PORT "$(get_env_value PORT 18080)"
  write_env_value SKYVAULT_VERSION "$(get_env_value SKYVAULT_VERSION 0.1.0)"
  write_env_value SKYVAULT_DATA_DIR "$(get_env_value SKYVAULT_DATA_DIR data)"
  write_env_value SKYVAULT_STORAGE_DIRS "$(get_env_value SKYVAULT_STORAGE_DIRS "$(get_env_value SKYVAULT_DATA_DIR data)/files")"
  write_env_value SKYVAULT_STORAGE_MODE "$(get_env_value SKYVAULT_STORAGE_MODE single)"
  write_env_value SKYVAULT_QUOTA_GB "$(get_env_value SKYVAULT_QUOTA_GB 5)"
  write_env_value SKYVAULT_MAX_RAM_MB "$(get_env_value SKYVAULT_MAX_RAM_MB 1024)"
  write_env_value SKYVAULT_CPU_CORES "$(get_env_value SKYVAULT_CPU_CORES 2)"
}

show_config() {
  config_text="SkyVault config: $env_file

Storage dir: $(get_env_value SKYVAULT_DATA_DIR data)
Storage locations: $(get_env_value SKYVAULT_STORAGE_DIRS "$(get_env_value SKYVAULT_DATA_DIR data)/files")
Storage mode: $(get_env_value SKYVAULT_STORAGE_MODE single)
Quota/account: $(get_env_value SKYVAULT_QUOTA_GB 5) GB
RAM limit: $(get_env_value SKYVAULT_MAX_RAM_MB 1024) MB
CPU cores: $(get_env_value SKYVAULT_CPU_CORES 2)
HTTP port: $(get_env_value PORT 18080)
Version: $(get_env_value SKYVAULT_VERSION 0.1.0)

SMTP host: $(get_env_value SMTP_HOST smtp.gmail.com)
SMTP port: $(get_env_value SMTP_PORT 587)
SMTP user: $(get_env_value SMTP_USER skyvaultcloud0@gmail.com)
SMTP pass: $(if [ -n "$(get_env_value SMTP_PASS '')" ]; then printf set; else printf empty; fi)
SMTP from: $(get_env_value SMTP_FROM skyvaultcloud0@gmail.com)
SMTP SSL: $(get_env_value SMTP_SSL true)"

  if [ "$use_tui" -eq 1 ]; then
    whiptail --title "SkyVault current config" --msgbox "$config_text" 22 78
    return
  fi

  printf '\n%s\n' "$config_text"
}

configure_storage() {
  while true; do
    choice="$(menu_select "Storage" "Wybierz opcje storage" 17 74 8 \
      1 "Folder metadanych: $(get_env_value SKYVAULT_DATA_DIR data)" \
      2 "Lokalizacje plikow: $(get_env_value SKYVAULT_STORAGE_DIRS "$(get_env_value SKYVAULT_DATA_DIR data)/files")" \
      3 "Tryb storage: $(get_env_value SKYVAULT_STORAGE_MODE single)" \
      4 "Quota na konto: $(get_env_value SKYVAULT_QUOTA_GB 5) GB" \
      5 "Pokaz storage" \
      0 "Wroc")" || return

    case "$choice" in
      1) set_value SKYVAULT_DATA_DIR 'Folder metadanych SkyVault' data ;;
      2) set_value SKYVAULT_STORAGE_DIRS 'Lokalizacje plikow, rozdzielone przecinkami' "$(get_env_value SKYVAULT_DATA_DIR data)/files" ;;
      3) set_value SKYVAULT_STORAGE_MODE 'Tryb storage: single/spread/mirror/raid0/raid1' single storage_mode ;;
      4) set_value SKYVAULT_QUOTA_GB 'Quota na konto w GB' 5 number ;;
      5) info_box "Storage" "Folder metadanych: $(get_env_value SKYVAULT_DATA_DIR data)
Lokalizacje plikow: $(get_env_value SKYVAULT_STORAGE_DIRS "$(get_env_value SKYVAULT_DATA_DIR data)/files")
Tryb storage: $(get_env_value SKYVAULT_STORAGE_MODE single)
Quota na konto: $(get_env_value SKYVAULT_QUOTA_GB 5) GB" ;;
      0) return ;;
      *) info_box "SkyVault" "Nieznana opcja." ;;
    esac
  done
}

configure_hardware() {
  while true; do
    choice="$(menu_select "Hardware" "Wybierz opcje hardware" 17 74 8 \
      1 "Limit RAM: $(get_env_value SKYVAULT_MAX_RAM_MB 1024) MB" \
      2 "Rdzenie CPU: $(get_env_value SKYVAULT_CPU_CORES 2)" \
      5 "Pokaz hardware" \
      0 "Wroc")" || return

    case "$choice" in
      1) set_value SKYVAULT_MAX_RAM_MB 'Limit RAM procesu w MB' 1024 number ;;
      2) set_value SKYVAULT_CPU_CORES 'Rdzenie CPU dla .NET' 2 number ;;
      5) info_box "Hardware" "Limit RAM: $(get_env_value SKYVAULT_MAX_RAM_MB 1024) MB
Rdzenie CPU: $(get_env_value SKYVAULT_CPU_CORES 2)" ;;
      0) return ;;
      *) info_box "SkyVault" "Nieznana opcja." ;;
    esac
  done
}

configure_network() {
  while true; do
    choice="$(menu_select "Network" "Wybierz opcje network" 16 74 7 \
      1 "Port HTTP: $(get_env_value PORT 18080)" \
      2 "Version/build: $(get_env_value SKYVAULT_VERSION 0.1.0)" \
      5 "Pokaz network" \
      0 "Wroc")" || return

    case "$choice" in
      1) set_value PORT 'Port HTTP' 18080 number ;;
      2) set_value SKYVAULT_VERSION 'Version/build' 0.1.0 ;;
      5) info_box "Network" "Port HTTP: $(get_env_value PORT 18080)
Version/build: $(get_env_value SKYVAULT_VERSION 0.1.0)" ;;
      0) return ;;
      *) info_box "SkyVault" "Nieznana opcja." ;;
    esac
  done
}

configure_smtp() {
  while true; do
    choice="$(menu_select "SMTP" "Wybierz opcje SMTP" 20 78 10 \
      1 "Host: $(get_env_value SMTP_HOST smtp.gmail.com)" \
      2 "Port: $(get_env_value SMTP_PORT 587)" \
      3 "User: $(get_env_value SMTP_USER skyvaultcloud0@gmail.com)" \
      4 "Password: $(if [ -n "$(get_env_value SMTP_PASS '')" ]; then printf set; else printf empty; fi)" \
      5 "From: $(get_env_value SMTP_FROM skyvaultcloud0@gmail.com)" \
      6 "SSL: $(get_env_value SMTP_SSL true)" \
      9 "Pokaz SMTP" \
      0 "Wroc")" || return

    case "$choice" in
      1) set_value SMTP_HOST 'SMTP host' smtp.gmail.com ;;
      2) set_value SMTP_PORT 'SMTP port' 587 number ;;
      3) set_value SMTP_USER 'SMTP user' skyvaultcloud0@gmail.com ;;
      4) set_secret_value SMTP_PASS 'SMTP pass' '' ;;
      5) set_value SMTP_FROM 'SMTP from' "$(get_env_value SMTP_USER skyvaultcloud0@gmail.com)" ;;
      6) set_value SMTP_SSL 'SMTP SSL true/false' true bool ;;
      9) info_box "SMTP" "Host: $(get_env_value SMTP_HOST smtp.gmail.com)
Port: $(get_env_value SMTP_PORT 587)
User: $(get_env_value SMTP_USER skyvaultcloud0@gmail.com)
Password: $(if [ -n "$(get_env_value SMTP_PASS '')" ]; then printf set; else printf empty; fi)
From: $(get_env_value SMTP_FROM skyvaultcloud0@gmail.com)
SSL: $(get_env_value SMTP_SSL true)" ;;
      0) return ;;
      *) info_box "SkyVault" "Nieznana opcja." ;;
    esac
  done
}

full_setup() {
  set_value SKYVAULT_DATA_DIR 'Folder metadanych SkyVault' data
  set_value SKYVAULT_STORAGE_DIRS 'Lokalizacje plikow, rozdzielone przecinkami' "$(get_env_value SKYVAULT_DATA_DIR data)/files"
  set_value SKYVAULT_STORAGE_MODE 'Tryb storage: single/spread/mirror/raid0/raid1' single storage_mode
  set_value SKYVAULT_QUOTA_GB 'Quota na konto w GB' 5 number
  set_value SKYVAULT_MAX_RAM_MB 'Limit RAM procesu w MB' 1024 number
  set_value SKYVAULT_CPU_CORES 'Rdzenie CPU dla .NET' 2 number
  set_value PORT 'Port HTTP' 18080 number
  set_value SKYVAULT_VERSION 'Version/build' 0.1.0
  set_value SMTP_HOST 'SMTP host' smtp.gmail.com
  set_value SMTP_PORT 'SMTP port' 587 number
  set_value SMTP_USER 'SMTP user' skyvaultcloud0@gmail.com
  set_secret_value SMTP_PASS 'SMTP pass' ''
  set_value SMTP_FROM 'SMTP from' "$(get_env_value SMTP_USER skyvaultcloud0@gmail.com)"
  set_value SMTP_SSL 'SMTP SSL true/false' true bool
  printf '\nPelna konfiguracja zapisana.\n'
}

first_run() {
  while true; do
    if [ "$use_tui" -eq 1 ]; then
      choice="$(whiptail --title "SkyVault first run" --menu "SkyVault nie ma jeszcze pliku .env." 15 70 5 \
        1 "Pelna konfiguracja krok po kroku" \
        2 "Utworz .env z ustawieniami default" \
        0 "Wyjdz" \
        3>&1 1>&2 2>&3)" || exit 0
    else
      printf '\nSkyVault nie ma jeszcze pliku .env.\n'
      printf '1. Pelna konfiguracja krok po kroku\n'
      printf '2. Utworz .env z ustawieniami default\n'
      printf '0. Wyjdz\n'
      printf 'Wybor: '
      IFS= read -r choice
    fi

    case "$choice" in
      1)
        set_default_config
        full_setup
        return
        ;;
      2)
        set_default_config
        if [ "$use_tui" -eq 1 ]; then
          whiptail --title "SkyVault" --msgbox ".env utworzony z ustawieniami default." 8 56
        else
          printf '.env utworzony z ustawieniami default.\n'
        fi
        return
        ;;
      0) exit 0 ;;
      *) printf 'Nieznana opcja.\n' ;;
    esac
  done
}

main_menu() {
  while true; do
    if [ "$use_tui" -eq 1 ]; then
      choice="$(whiptail --title "SkyVault console config" --menu "Wybierz sekcje konfiguracji" 19 72 9 \
        1 "Hardware: RAM i rdzenie CPU" \
        2 "Storage: folder danych i quota" \
        3 "Network: port HTTP i version" \
        4 "SMTP: poczta i weryfikacja" \
        5 "Pokaz obecna konfiguracje" \
        6 "Pelna konfiguracja krok po kroku" \
        7 "Uzupelnij defaulty" \
        0 "Wyjdz" \
        3>&1 1>&2 2>&3)" || exit 0
    else
      printf '\nSkyVault console config\n'
      printf '1. Hardware\n'
      printf '2. Storage\n'
      printf '3. Network\n'
      printf '4. SMTP\n'
      printf '5. Pokaz obecna konfiguracje\n'
      printf '6. Pelna konfiguracja krok po kroku\n'
      printf '7. Uzupelnij defaulty\n'
      printf '0. Wyjdz\n'
      printf 'Wybor: '
      IFS= read -r choice
    fi

    case "$choice" in
      1) configure_hardware; pause ;;
      2) configure_storage; pause ;;
      3) configure_network; pause ;;
      4) configure_smtp; pause ;;
      5) show_config; pause ;;
      6) full_setup; pause ;;
      7) set_default_config; printf 'Defaulty uzupelnione.\n'; pause ;;
      0) exit 0 ;;
      *) printf 'Nieznana opcja.\n'; pause ;;
    esac
  done
}

if [ ! -f "$env_file" ]; then
  first_run
fi

main_menu
