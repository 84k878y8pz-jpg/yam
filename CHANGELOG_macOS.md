# MetaCyber Agent - macOS Edition
## سجل التغييرات والتحويلات من Windows

---

## إصلاحات v3.5.5 (2026-05-04)

### إصلاح 1: خروج Agent UI عند فشل جلب الإعدادات (حرج)
**الملف**: `MetacyberAgent.Service/AgentUiWorker.cs`  
**المشكلة**: كان Agent UI يخرج فوراً عند فشل جلب الإعدادات من الخادم (`return` عند `null`) مما يخلق حلقة لا نهاية لها مع Watchdog.  
**الحل**: عرض علامة مائية افتراضية (اسم المستخدم) + إعادة المحاولة كل 60 ثانية حتى ينجح الاتصال.

### إصلاح 2: إضافة حالة `IsActive=false` للسجلات
**الملف**: `MetacyberAgent.Service/AgentUiWorker.cs`  
**المشكلة**: لم يكن هناك تسجيل عند عدم نشاط العلامة المائية.  
**الحل**: إضافة `else` يسجل حالة `IsActive=false`.

---

## إصلاحات v3.5.4 (2026-05-04)

### إصلاح 1: تعارض Mutex بين Service Mode و UI Mode (حرج)
**الملف**: `MetacyberAgent.Service/Program.cs`  
**المشكلة**: كلا الوضعين كانا يستخدمان نفس اسم Mutex مما يجعل Agent UI يخرج فوراً عند كل محاولة تشغيل.  
**الحل**: تغيير اسم Mutex ليكون مختلفاً لكل وضع:
- Service: `MetacyberAgent_macOS_Service`
- UI: `MetacyberAgent_macOS_UI_{username}`

### إصلاح 2: تسجيل PID خاطئ في MacProcessGuard
**الملف**: `MetacyberAgent.Service/AgentWorker.cs`  
**المشكلة**: كان يتم تسجيل PID لعملية `launchctl` وليس لـ Agent UI الفعلية، مما يجعل Watchdog يعتقد أن Agent UI توقف.  
**الحل**: استخدام `pgrep` للحصول على PID الفعلي بعد 2.5 ثانية من الإطلاق.

### إصلاح 3: إنشاء مجلد سجلات UI تلقائياً
**الملف**: `MetacyberAgent.Service/Program.cs`  
**المشكلة**: كان مجلد سجلات UI غير موجود عند أول تشغيل.  
**الحل**: إضافة إنشاء المجلد تلقائياً في وضع UI كما هو موجود في وضع Service.

---

## ملخص التحويل

تم تحويل MetaCyber Agent v3.5.3 من Windows إلى macOS مع الحفاظ على **جميع الخصائص والإصلاحات** الأصلية.

---

## جدول مقارنة التقنيات

| المكوّن | Windows (الأصلي) | macOS (الجديد) |
|---------|-----------------|----------------|
| نظام الخدمة | Windows Service (SCM) | LaunchDaemon (launchd) |
| مراقبة الجلسات | WTS API / WtsSessionMonitor | Console User Polling / MacSessionMonitor |
| إطلاق العملية في جلسة المستخدم | CreateProcessAsUser (Win32) | `launchctl asuser <UID>` |
| واجهة العلامة المائية | WinForms (System.Drawing) | Python tkinter / NSWindow |
| كشف الجهاز الافتراضي | WMI (Win32_ComputerSystem) | `system_profiler SPHardwareDataType` |
| قياس الذاكرة | WMI (Win32_OperatingSystem) | `vm_stat` + `sysctl hw.memsize` |
| رصد الطباعة | WMI (Win32_PrintJob) | CUPS access_log + `lpstat` |
| رصد التقاط الشاشة | WMI Events | FileSystemWatcher على مجلد Screenshots |
| إشارة الإيقاف الطبيعي | Named Event (Global\\) + File Flag | File Flag فقط |
| مثبّت التطبيق | WiX MSI | Bash Script + pkg (اختياري) |
| مسار السجلات | `%ProgramData%\MetacyberAgent\logs` | `/Library/Logs/MetacyberAgent/` |
| مسار الدعم | `%ProgramData%\MetacyberAgent` | `/Library/Application Support/MetacyberAgent/` |
| مسار التثبيت | `C:\Program Files\MetacyberAgent` | `/Library/MetacyberAgent/` |

---

## الإصلاحات المحافظ عليها (v3.5.3)

### الإصلاح 1: منع Tamper مزدوج
**الحل على macOS**: نفس آلية deduplication بقاموس `_lastTamperSent` في `MacProcessGuard.cs`
- ثابت `TAMPER_DEDUP_SECONDS = 10`
- دالتا `ShouldSendTamper()` و `RecordTamperSent()`

### الإصلاح 2: تسريع ظهور العلامة المائية
**الحل على macOS**: نفس الأوقات المحسّنة في `AgentUiWorker.cs` و `ApiService.cs`
- `NETWORK_WAIT_MAX_MS = 15,000`
- `HTTP_TIMEOUT_SECONDS = 8`
- `RETRY_DELAY_SECONDS = 3`

### الإصلاح 3: كشف حسابات النظام (username$)
**الحل على macOS**: في `AgentStatusService.cs`
- فحص `root` بدلاً من `SYSTEM`
- فحص `$USER` من متغيرات البيئة كـ fallback
- دالة `GetRealUsername()` في `AgentUiWorker.cs`

### الإصلاح 4: إلغاء التثبيت بدون إعادة تشغيل
**الحل على macOS**: `uninstall.sh`
- إيقاف الخدمات أولاً قبل الحذف
- `pkill` لإنهاء جميع العمليات
- لا حاجة لإعادة تشغيل الجهاز

---

## هيكل المشروع

```
MetaCyber_Agent_macOS/
├── MetacyberAgent.Core/          # المنطق المشترك (محوّل لـ macOS)
│   ├── WatermarkSettings.cs      # إعدادات العلامة المائية (بدون تغيير)
│   ├── ApiService.cs             # التواصل مع API (بدون تغيير)
│   ├── AgentStatusService.cs     # حالة الـ Agent (WMI → sysctl/vm_stat)
│   ├── GracefulShutdownHelper.cs # إشارة الإيقاف (Named Event → File Flag)
│   ├── TamperLogService.cs       # سجل العبث (بدون تغيير)
│   ├── ScpEnforcementService.cs  # منع التقاط (WMI → Process monitoring)
│   ├── CaptureLogService.cs      # سجل التقاط (WMI → FileSystemWatcher)
│   ├── PrintLogService.cs        # سجل الطباعة (WMI → CUPS logs)
│   └── PeekControlService.cs     # التحكم في الرؤية
│
├── MetacyberAgent.Service/       # الخدمة الرئيسية
│   ├── Program.cs                # نقطة الدخول (Windows Service → launchd)
│   ├── AgentWorker.cs            # Worker الخدمة (Session 0 → root daemon)
│   ├── AgentUiWorker.cs          # Worker العلامة المائية (WinForms → tkinter)
│   ├── MacSessionMonitor.cs      # مراقبة الجلسات (WTS → Console User)
│   ├── MacProcessGuard.cs        # حارس العمليات (Win32 → POSIX)
│   └── appsettings.json          # الإعدادات
│
├── MetacyberAgent.Watchdog/      # الـ Watchdog المستقل
│   └── Program.cs                # (WTS/Win32 → launchctl)
│
├── pkg/                          # ملفات النشر
│   ├── com.metacyber.agent.plist       # LaunchDaemon
│   ├── com.metacyber.agent.ui.plist    # LaunchAgent
│   └── com.metacyber.watchdog.plist    # Watchdog LaunchDaemon
│
└── scripts/                      # سكريبتات البناء والتثبيت
    ├── build.sh                  # بناء المشروع
    ├── install.sh                # التثبيت
    ├── uninstall.sh              # إلغاء التثبيت
    └── watermark_overlay.py      # عرض العلامة المائية
```

---

## ملاحظات تقنية مهمة

### لماذا tkinter للعلامة المائية؟
على macOS، لا تتوفر WinForms. البدائل المتاحة:
1. **tkinter** (Python): متوفر مسبقاً على macOS، سهل التخصيص ✅
2. **NSWindow** (Objective-C/Swift): يتطلب Xcode وتوقيع رقمي
3. **Electron**: ثقيل جداً

### لماذا Console User Polling بدلاً من WTS?
macOS لا يدعم WTS API. بدائل:
1. **stat -f %Su /dev/console**: الأبسط والأكثر موثوقية ✅
2. **SCDynamicStore**: يتطلب Objective-C
3. **NSDistributedNotificationCenter**: يتطلب Objective-C

### لماذا launchctl asuser بدلاً من CreateProcessAsUser?
`CreateProcessAsUser` هي Win32 API حصرية. على macOS:
- `launchctl asuser <UID> <command>`: يُشغّل الأمر في سياق المستخدم ✅
- `sudo -u <username>`: بديل أبسط لكن أقل تحكماً
