#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
# MetaCyber Agent - macOS Installation Script
# الإصدار: 3.5.3
# ═══════════════════════════════════════════════════════════════════════

set -e

# ─── التحقق من الصلاحيات ─────────────────────────────────────────────
if [ "$EUID" -ne 0 ]; then
    echo "❌ يجب تشغيل هذا السكريبت بصلاحيات root:"
    echo "   sudo bash install.sh"
    exit 1
fi

# ─── المتغيرات ───────────────────────────────────────────────────────
INSTALL_DIR="/Library/MetacyberAgent"
DAEMON_DIR="/Library/LaunchDaemons"
AGENT_DIR="/Library/LaunchAgents"
LOG_DIR="/Library/Logs/MetacyberAgent"
SUPPORT_DIR="/Library/Application Support/MetacyberAgent"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$(dirname "$SCRIPT_DIR")"

echo "═══════════════════════════════════════════════════════════════"
echo "  MetaCyber Agent - macOS Installer v3.5.3"
echo "═══════════════════════════════════════════════════════════════"

# ─── إيقاف الخدمات الحالية (إن وجدت) ────────────────────────────────
echo "⏹️  إيقاف الخدمات الحالية..."
launchctl unload "$DAEMON_DIR/com.metacyber.agent.plist"    2>/dev/null || true
launchctl unload "$DAEMON_DIR/com.metacyber.watchdog.plist" 2>/dev/null || true

# إيقاف LaunchAgent لجميع المستخدمين
for uid in $(dscl . -list /Users UniqueID | awk '$2 >= 500 {print $2}'); do
    launchctl asuser "$uid" launchctl unload \
        "$AGENT_DIR/com.metacyber.agent.ui.plist" 2>/dev/null || true
done

sleep 2

# ─── إنشاء المجلدات ──────────────────────────────────────────────────
echo "📁 إنشاء المجلدات..."
mkdir -p "$INSTALL_DIR"
mkdir -p "$LOG_DIR"
mkdir -p "$SUPPORT_DIR"

# ─── نسخ الملفات التنفيذية ───────────────────────────────────────────
echo "📦 نسخ الملفات..."

# الملف التنفيذي الرئيسي
if [ -f "$PACKAGE_DIR/publish/MetacyberAgentService" ]; then
    cp "$PACKAGE_DIR/publish/MetacyberAgentService" "$INSTALL_DIR/"
    chmod +x "$INSTALL_DIR/MetacyberAgentService"
else
    echo "❌ ملف MetacyberAgentService غير موجود في publish/"
    echo "   قم ببناء المشروع أولاً: dotnet publish -c Release -r osx-arm64"
    exit 1
fi

# الـ Watchdog
if [ -f "$PACKAGE_DIR/publish/MetacyberAgent.Watchdog" ]; then
    cp "$PACKAGE_DIR/publish/MetacyberAgent.Watchdog" "$INSTALL_DIR/"
    chmod +x "$INSTALL_DIR/MetacyberAgent.Watchdog"
fi

# سكريبت العلامة المائية
cp "$SCRIPT_DIR/watermark_overlay.py" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/watermark_overlay.py"

# ملف الإعدادات (إذا لم يكن موجوداً)
if [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
    cp "$PACKAGE_DIR/MetacyberAgent.Service/appsettings.json" "$INSTALL_DIR/"
    echo "⚙️  تم نسخ appsettings.json - يرجى تعديل ServerUrl و ProductId"
fi

# ─── ضبط الصلاحيات ───────────────────────────────────────────────────
echo "🔒 ضبط الصلاحيات..."
chown -R root:wheel "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR"
chmod 644 "$INSTALL_DIR/appsettings.json"
chmod 755 "$LOG_DIR"
chmod 755 "$SUPPORT_DIR"

# ─── تثبيت LaunchDaemons ─────────────────────────────────────────────
echo "🔧 تثبيت LaunchDaemons..."
cp "$PACKAGE_DIR/pkg/com.metacyber.agent.plist"    "$DAEMON_DIR/"
cp "$PACKAGE_DIR/pkg/com.metacyber.watchdog.plist" "$DAEMON_DIR/"
chown root:wheel "$DAEMON_DIR/com.metacyber.agent.plist"
chown root:wheel "$DAEMON_DIR/com.metacyber.watchdog.plist"
chmod 644 "$DAEMON_DIR/com.metacyber.agent.plist"
chmod 644 "$DAEMON_DIR/com.metacyber.watchdog.plist"

# ─── تثبيت LaunchAgent ───────────────────────────────────────────────
echo "🔧 تثبيت LaunchAgent..."
cp "$PACKAGE_DIR/pkg/com.metacyber.agent.ui.plist" "$AGENT_DIR/"
chown root:wheel "$AGENT_DIR/com.metacyber.agent.ui.plist"
chmod 644 "$AGENT_DIR/com.metacyber.agent.ui.plist"

# ─── تشغيل الخدمات ───────────────────────────────────────────────────
echo "▶️  تشغيل الخدمات..."
launchctl load -w "$DAEMON_DIR/com.metacyber.agent.plist"
launchctl load -w "$DAEMON_DIR/com.metacyber.watchdog.plist"

# تشغيل LaunchAgent للمستخدم الحالي
CURRENT_USER=$(stat -f %Su /dev/console 2>/dev/null || echo "")
if [ -n "$CURRENT_USER" ] && [ "$CURRENT_USER" != "root" ]; then
    CURRENT_UID=$(id -u "$CURRENT_USER")
    launchctl asuser "$CURRENT_UID" launchctl load -w \
        "$AGENT_DIR/com.metacyber.agent.ui.plist" 2>/dev/null || true
    echo "✅ تم تشغيل Agent UI للمستخدم: $CURRENT_USER"
fi

# ─── التحقق من التثبيت ───────────────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "✅ تم التثبيت بنجاح!"
echo ""
echo "📋 الخطوات التالية:"
echo "   1. عدّل ملف الإعدادات: sudo nano $INSTALL_DIR/appsettings.json"
echo "   2. أضف ServerUrl و ProductId"
echo "   3. أعد تشغيل الخدمة: sudo launchctl kickstart -k system/com.metacyber.agent"
echo ""
echo "📊 فحص حالة الخدمات:"
echo "   sudo launchctl list com.metacyber.agent"
echo "   sudo launchctl list com.metacyber.watchdog"
echo ""
echo "📄 السجلات:"
echo "   tail -f $LOG_DIR/service-*.log"
echo "═══════════════════════════════════════════════════════════════"
