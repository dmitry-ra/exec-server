#!/usr/bin/env bash
# Create a new isolated Ubuntu 26.04 test VM. Existing guests/files are refused.
set -euo pipefail
cd "$(dirname "$0")/.."
vm_name=exec-server-test-20261116
cache_dir="${EXECSERVER_LAB_CACHE:-/mnt/data/exec-server-lab-inputs}"
disk="/mnt/vms/images/$vm_name.qcow2"
seed="/mnt/vms/cloud-init/$vm_name-seed.img"
base="$cache_dir/ubuntu-26.04-7b90593f.img"
image_url=https://cloud-images.ubuntu.com/releases/26.04/release/ubuntu-26.04-server-cloudimg-amd64.img
image_sha=7b90593f5532b49b7e3fe3d2743f0d1f0d822e5b07cebcd8e00c0df8069ecab9
if sudo virsh dominfo "$vm_name" >/dev/null 2>&1 || [[ -e "$disk" || -e "$seed" ]]; then
  echo 'Refusing an existing VM or lab file.' >&2
  exit 1
fi
mkdir -p "$cache_dir" .artifacts/linux-vm
if [[ ! -f "$base" ]]; then
  curl --fail --location --retry 2 "$image_url" --output "$base"
fi
printf '%s  %s\n' "$image_sha" "$base" | sha256sum --check --status
work="$PWD/.artifacts/linux-vm"
umask 077
cat > "$work/user-data" <<EOF
#cloud-config
hostname: $vm_name
users:
  - name: runner
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    ssh_authorized_keys:
      - $(cat "$HOME/.ssh/id_ed25519.pub")
package_update: false
EOF
printf 'instance-id: %s\nlocal-hostname: %s\n' "$vm_name" "$vm_name" > "$work/meta-data"
sudo cloud-localds "$seed" "$work/user-data" "$work/meta-data"
sudo qemu-img create -f qcow2 -F qcow2 -b "$base" "$disk" 35G
sudo chown libvirt-qemu:kvm "$disk" "$seed"
sudo virt-install --name "$vm_name" --memory 4096 --vcpus 2 \
  --disk "path=$disk,device=disk,bus=virtio" --disk "path=$seed,device=cdrom" \
  --os-variant ubuntu24.04 --network network=default --graphics none --noautoconsole --import
for attempt in $(seq 1 60); do
  address="$(sudo virsh domifaddr "$vm_name" | awk '/ipv4/ {sub(/\/.*/, "", $4); print $4; exit}')"
  if [[ -n "$address" ]]; then
    printf '%s\n' "$address" > "$work/address"
    echo "Created $vm_name at $address; cloud-init may still be finishing."
    exit 0
  fi
  sleep 3
done
echo 'VM exists but no DHCP address appeared within 180 seconds.' >&2
exit 1
