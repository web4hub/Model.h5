#!/bin/bash
# BSD Zero Clause License
# 
# Copyright (c) 2026 Sung-jin Hong
#
# Permission to use, copy, modify, and/or distribute this software for any
# purpose with or without fee is hereby granted.
#
# THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
# REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
# AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
# INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
# LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
# OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
# PERFORMANCE OF THIS SOFTWARE.
#
#
# Fan control for Raspberry Pi 5 running Talos Linux
# Programs the RP1 PWM hardware directly via a privileged pod,
# bypassing the missing pwm-rp1 kernel driver.
#
# Based on kernel sources:
#   drivers/pwm/pwm-rp1.c       - PWM register layout
#   drivers/clk/clk-rp1.c       - Clock enable registers
#   arch/arm64/boot/dts/broadcom/rp1.dtsi - Device tree config
#
# Fan uses PWM1 (base=0x9c000) channel 3, GPIO45 (FUNCSEL=2).
# Device tree: period=41566ns (~24kHz), polarity=inverted, clock=50MHz.
#
# Usage:
#   ./fan-control.sh <speed-percent>  Set fan speed (0-100)
#   ./fan-control.sh status           Show current temperature
#   ./fan-control.sh off              Turn fan off
#   ./fan-control.sh max              Set fan to 100%

set -euo pipefail

NODE="${FAN_NODE:-talos-z7p-qtb}"
NAMESPACE="kube-system"
POD_NAME="fan-ctl"
CM_NAME="fan-ctl-script"

cleanup() {
    kubectl delete pod "$POD_NAME" -n "$NAMESPACE" --ignore-not-found=true > /dev/null 2>&1
    kubectl delete configmap "$CM_NAME" -n "$NAMESPACE" --ignore-not-found=true > /dev/null 2>&1
}

usage() {
    echo "Usage: $0 <speed-percent|status|off|max>"
    echo ""
    echo "  <0-100>   Set fan speed percentage"
    echo "  status    Show current temperature"
    echo "  off       Turn fan off"
    echo "  max       Set fan to 100%"
    exit 1
}

if [ $# -lt 1 ]; then
    usage
fi

case "$1" in
    status)
        echo "Temperature: $(talosctl read /sys/class/thermal/thermal_zone0/temp --nodes 192.168.1.25 2>/dev/null | awk '{printf "%.1f°C\n", $1/1000}')"
        exit 0
        ;;
    off)
        SPEED_PERCENT=0
        ;;
    max)
        SPEED_PERCENT=100
        ;;
    *)
        SPEED_PERCENT="$1"
        if ! [[ "$SPEED_PERCENT" =~ ^[0-9]+$ ]] || [ "$SPEED_PERCENT" -gt 100 ]; then
            echo "Error: speed must be 0-100"
            exit 1
        fi
        ;;
esac

echo "Setting fan to ${SPEED_PERCENT}%..."

cleanup

# Write Python script to a temp file to avoid shell escaping issues
TMPFILE=$(mktemp)
cat > "$TMPFILE" << 'PYEOF'
import mmap, struct, os, sys

SPEED = int(sys.argv[1])

RESOURCE1 = "/host-sys/bus/pci/devices/0002:01:00.0/resource1"
fd = os.open(RESOURCE1, os.O_RDWR | os.O_SYNC)
mm = mmap.mmap(fd, 4 * 1024 * 1024, mmap.MAP_SHARED, mmap.PROT_READ | mmap.PROT_WRITE)

def read32(offset):
    return struct.unpack_from("<I", mm, offset)[0]

def write32(offset, val):
    struct.pack_into("<I", mm, offset, val)

# --- RP1 Clock Controller (base=0x18000) ---
# PWM1 clock registers (from clk-rp1.c)
CLK_PWM1_CTRL     = 0x18084
CLK_PWM1_DIV_INT  = 0x18088
CLK_PWM1_DIV_FRAC = 0x1808c
CLK_PWM1_SEL      = 0x18090
CLK_CTRL_ENABLE   = 1 << 11

# PWM1 aux parents (from clk-rp1.c, num_std_parents=0, all aux):
#   0: "" (empty)       4: clksrc_gp1
#   1: pll_video_sec    5: clksrc_gp2
#   2: xosc (50MHz)     6: clksrc_gp3
#   3: clksrc_gp0       7: clksrc_gp4  8: clksrc_gp5
# AUXSRC is in CTRL bits [9:5]. We want xosc (index 2).
AUXSRC_XOSC = 2

# Configure clock: disable first, set source, then enable
clk_ctrl = read32(CLK_PWM1_CTRL)

# Disable clock before changing source
write32(CLK_PWM1_CTRL, clk_ctrl & ~CLK_CTRL_ENABLE)

# Set divider: INT=1, FRAC=0 (xosc 50MHz pass-through)
write32(CLK_PWM1_DIV_INT, 1)
write32(CLK_PWM1_DIV_FRAC, 0)

# Set AUXSRC to xosc (bits [9:5] = 2) and SRC to AUX_SEL (bit 0 = 1)
clk_ctrl = read32(CLK_PWM1_CTRL)
clk_ctrl &= ~0x3E1          # Clear AUXSRC [9:5] and SRC [0]
clk_ctrl |= (AUXSRC_XOSC << 5)  # AUXSRC = 2 (xosc)
clk_ctrl |= 1                    # SRC = 1 (AUX_SEL)
clk_ctrl |= CLK_CTRL_ENABLE      # Enable
write32(CLK_PWM1_CTRL, clk_ctrl)

# Set SEL register (one-hot) - bit 1 for aux source
write32(CLK_PWM1_SEL, 1 << 1)

print(f"PWM1 clock: configured (CTRL=0x{read32(CLK_PWM1_CTRL):08x} SEL=0x{read32(CLK_PWM1_SEL):08x})")

# --- RP1 GPIO (base=0xd0000) ---
# Pin 45 is bank 2 (pins 34-53), local pin 11
# PIN(45, pwm1, i2c5, spi7, spi6, i2s2, gpio, proc_rio, _, _)
# FUNCSEL=0 is pwm1 for GPIO45 (from pinctrl-rp1.c)
GPIO45_CTRL = 0xd0000 + 0x8000 + 11 * 8 + 4
PWM_FUNCSEL = 0

ctrl = read32(GPIO45_CTRL)
if (ctrl & 0x1f) != PWM_FUNCSEL:
    write32(GPIO45_CTRL, (ctrl & ~0x1f) | PWM_FUNCSEL)
    print(f"GPIO45: switched to PWM mode (FUNCSEL={PWM_FUNCSEL})")

# --- RP1 PWM1 (base=0x9c000) ---
# Register layout from pwm-rp1.c:
#   GLOBAL_CTRL     = 0x000
#   CHANNEL_CTRL(x) = 0x014 + x*16
#   RANGE(x)        = 0x018 + x*16
#   DUTY(x)         = 0x020 + x*16
PWM1      = 0x9c000
CH        = 3
GLOB_CTRL = PWM1 + 0x000
CHAN_CTRL  = PWM1 + 0x014 + CH * 16  # 0x9c044
RANGE_REG = PWM1 + 0x018 + CH * 16  # 0x9c048
DUTY_REG  = PWM1 + 0x020 + CH * 16  # 0x9c050

# Clock = 50MHz = 20ns/tick, period = 41566ns
RANGE_TICKS = 41566 // 20  # = 2078

# With HW polarity inversion (BIT(3)), RP1 flips the output:
#   duty=0 → output LOW → fan off
#   duty=range → output HIGH → fan max
DUTY_TICKS = RANGE_TICKS * SPEED // 100

# 1. Initialize channel: BIT(8)=FIFO_POP_MASK | BIT(3)=POLARITY_INV | BIT(0)=M/S_MODE
write32(CHAN_CTRL, 0x109)

# 2. Set duty and period in clock ticks
write32(DUTY_REG, DUTY_TICKS)
write32(RANGE_REG, RANGE_TICKS)

# 3. Enable channel 3: set BIT(3) in GLOBAL_CTRL
glob = read32(GLOB_CTRL)
glob |= (1 << CH)
write32(GLOB_CTRL, glob)

# 4. Trigger update: set SET_UPDATE BIT(31) separately
glob = read32(GLOB_CTRL)
glob |= (1 << 31)
write32(GLOB_CTRL, glob)

print(f"Fan: {SPEED}% (duty={DUTY_TICKS}/{RANGE_TICKS} ticks, inverted)")

with open("/host-sys/class/thermal/thermal_zone0/temp") as f:
    temp = int(f.read().strip()) / 1000
    print(f"Temperature: {temp:.1f}C")

mm.close()
os.close(fd)
PYEOF

kubectl create configmap "$CM_NAME" -n "$NAMESPACE" --from-file=fan.py="$TMPFILE" > /dev/null
rm -f "$TMPFILE"

# Run the pod
kubectl run "$POD_NAME" \
    --namespace="$NAMESPACE" \
    --image=python:3.12-slim \
    --restart=Never \
    --overrides="{\"spec\":{\"nodeName\":\"${NODE}\",\"containers\":[{\"name\":\"fan-ctl\",\"image\":\"python:3.12-slim\",\"command\":[\"python3\",\"/scripts/fan.py\",\"${SPEED_PERCENT}\"],\"securityContext\":{\"privileged\":true},\"volumeMounts\":[{\"name\":\"sys\",\"mountPath\":\"/host-sys\"},{\"name\":\"scripts\",\"mountPath\":\"/scripts\"}]}],\"volumes\":[{\"name\":\"sys\",\"hostPath\":{\"path\":\"/sys\"}},{\"name\":\"scripts\",\"configMap\":{\"name\":\"${CM_NAME}\"}}]}}" \
    > /dev/null

# Wait for pod to complete
kubectl wait --for=jsonpath='{.status.phase}'=Succeeded pod/"$POD_NAME" -n "$NAMESPACE" --timeout=60s > /dev/null 2>&1
kubectl logs "$POD_NAME" -n "$NAMESPACE" 2>/dev/null

# Cleanup
cleanup
