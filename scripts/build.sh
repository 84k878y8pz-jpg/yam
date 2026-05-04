#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
# MetaCyber Agent - macOS Build Script
# يبني المشروع لـ Apple Silicon (arm64) و Intel (x64)
# ═══════════════════════════════════════════════════════════════════════

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="$PROJECT_DIR/publish"

echo "═══════════════════════════════════════════════════════════════"
echo "  MetaCyber Agent - macOS Build"
echo "═══════════════════════════════════════════════════════════════"

# ─── التحقق من .NET SDK ──────────────────────────────────────────────
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET SDK غير مثبت"
    echo "   قم بتثبيته من: https://dot.net/download"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version)
echo "✅ .NET SDK: $DOTNET_VERSION"

# ─── تحديد المعمارية ─────────────────────────────────────────────────
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
    echo "🍎 البناء لـ Apple Silicon (arm64)"
else
    RID="osx-x64"
    echo "🍎 البناء لـ Intel Mac (x64)"
fi

# يمكن تجاوز المعمارية عبر متغير البيئة
RID="${TARGET_RID:-$RID}"
echo "🎯 Runtime Identifier: $RID"

# ─── تنظيف المخرجات السابقة ──────────────────────────────────────────
echo ""
echo "🧹 تنظيف المخرجات السابقة..."
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

# ─── بناء MetacyberAgent.Service ─────────────────────────────────────
echo ""
echo "🔨 بناء MetacyberAgent.Service..."
dotnet publish "$PROJECT_DIR/MetacyberAgent.Service/MetacyberAgent.Service.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -o "$PUBLISH_DIR/service"

# نسخ الملف التنفيذي
cp "$PUBLISH_DIR/service/MetacyberAgentService" "$PUBLISH_DIR/"
chmod +x "$PUBLISH_DIR/MetacyberAgentService"
echo "✅ MetacyberAgentService جاهز"

# ─── بناء MetacyberAgent.Watchdog ────────────────────────────────────
echo ""
echo "🔨 بناء MetacyberAgent.Watchdog..."
dotnet publish "$PROJECT_DIR/MetacyberAgent.Watchdog/MetacyberAgent.Watchdog.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:RuntimeIdentifier="$RID" \
    -o "$PUBLISH_DIR/watchdog"

# نسخ الملف التنفيذي
cp "$PUBLISH_DIR/watchdog/MetacyberAgent.Watchdog" "$PUBLISH_DIR/"
chmod +x "$PUBLISH_DIR/MetacyberAgent.Watchdog"
echo "✅ MetacyberAgent.Watchdog جاهز"

# ─── نسخ الملفات الإضافية ────────────────────────────────────────────
echo ""
echo "📋 نسخ الملفات الإضافية..."
cp "$SCRIPT_DIR/watermark_overlay.py" "$PUBLISH_DIR/"
cp "$PROJECT_DIR/MetacyberAgent.Service/appsettings.json" "$PUBLISH_DIR/"

# ─── ملخص البناء ─────────────────────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "✅ اكتمل البناء بنجاح!"
echo ""
echo "📦 الملفات في: $PUBLISH_DIR/"
ls -lh "$PUBLISH_DIR/"
echo ""
echo "📋 الخطوات التالية:"
echo "   1. عدّل appsettings.json: nano $PUBLISH_DIR/appsettings.json"
echo "   2. شغّل التثبيت: sudo bash $SCRIPT_DIR/install.sh"
echo "═══════════════════════════════════════════════════════════════"
