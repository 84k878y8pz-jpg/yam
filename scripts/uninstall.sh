#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
# MetaCyber Agent - macOS Uninstallation Script
# ═══════════════════════════════════════════════════════════════════════

set -e

if [ "$EUID" -ne 0 ]; then
    echo "❌ يجب تشغيل هذا السكريبت بصلاحيات root:"
    echo "   sudo bash uninstall.sh"
    exit 1
fi

INSTALL_DIR="/Library/MetacyberAgent"
DAEMON_DIR="/Library/LaunchDaemons"
AGENT_DIR="/Library/LaunchAgents"
LOG_DIR="/Library/Logs/MetacyberAgent"
SUPPORT_DIR="/Library/Application Support/MetacyberAgent"

echo "═══════════════════════════════════════════════════════════════"
echo "  MetaCyber Agent - macOS Uninstaller"
echo "═══════════════════════════════════════════════════════════════"

# ─── إيقاف وإلغاء تحميل الخدمات ─────────────────────────────────────
echo "⏹️  إيقاف الخدمات..."

# إيقاف LaunchDaemons
launchctl unload -w "$DAEMON_DIR/com.metacyber.watchdog.plist" 2>/dev/null || true
launchctl unload -w "$DAEMON_DIR/com.metacyber.agent.plist"    2>/dev/null || true

# إيقاف LaunchAgent لجميع المستخدمين
for uid in $(dscl . -list /Users UniqueID | awk '$2 >= 500 {print $2}'); do
    launchctl asuser "$uid" launchctl unload -w \
        "$AGENT_DIR/com.metacyber.agent.ui.plist" 2>/dev/null || true
done

# إنهاء العمليات المتبقية
pkill -f "MetacyberAgentService" 2>/dev/null || true
pkill -f "MetacyberAgent.Watchdog" 2>/dev/null || true
pkill -f "watermark_overlay.py" 2>/dev/null || true

sleep 2

# ─── حذف ملفات plist ─────────────────────────────────────────────────
echo "🗑️  حذف ملفات الخدمة..."
rm -f "$DAEMON_DIR/com.metacyber.agent.plist"
rm -f "$DAEMON_DIR/com.metacyber.watchdog.plist"
rm -f "$AGENT_DIR/com.metacyber.agent.ui.plist"

# ─── حذف ملفات التطبيق ───────────────────────────────────────────────
echo "🗑️  حذف ملفات التطبيق..."
rm -rf "$INSTALL_DIR"

# ─── حذف السجلات (اختياري) ───────────────────────────────────────────
read -p "هل تريد حذف السجلات؟ [y/N]: " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    rm -rf "$LOG_DIR"
    rm -rf "$SUPPORT_DIR"
    echo "✅ تم حذف السجلات"
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "✅ تم إلغاء التثبيت بنجاح - لا حاجة لإعادة تشغيل الجهاز"
echo "═══════════════════════════════════════════════════════════════"
