#!/usr/bin/env python3
"""
MetaCyber Agent - Watermark Overlay for macOS
يعرض علامة مائية شفافة فوق جميع النوافذ باستخدام tkinter
(بديل لـ WinForms على Windows)

الاستخدام:
    python3 watermark_overlay.py --text "USERNAME" --opacity 0.5 \
        --font-size 14 --color "#FFFFFF" --position "bottomRight"
"""

import argparse
import sys
import os
import threading
import time

try:
    import tkinter as tk
    from tkinter import font as tkfont
except ImportError:
    print("ERROR: tkinter غير متوفر. قم بتثبيت python3-tk", file=sys.stderr)
    sys.exit(1)

# ─── تحليل المعاملات ──────────────────────────────────────────────────
parser = argparse.ArgumentParser(description="MetaCyber Watermark Overlay")
parser.add_argument("--text",      default="MetaCyber",  help="نص العلامة المائية")
parser.add_argument("--opacity",   type=float, default=0.5, help="الشفافية (0.0-1.0)")
parser.add_argument("--font-size", type=int,   default=14,  help="حجم الخط")
parser.add_argument("--color",     default="#FFFFFF",    help="لون النص (hex)")
parser.add_argument("--position",  default="bottomRight",help="الموضع")
parser.add_argument("--scrolling", action="store_true",  help="نص متحرك")
parser.add_argument("--scroll-speed", type=int, default=50, help="سرعة التمرير")
args = parser.parse_args()

# ─── تحويل اللون من Hex إلى RGB ──────────────────────────────────────
def hex_to_rgb(hex_color):
    hex_color = hex_color.lstrip('#')
    return tuple(int(hex_color[i:i+2], 16) for i in (0, 2, 4))

# ─── إنشاء نافذة العلامة المائية ─────────────────────────────────────
class WatermarkOverlay:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("MetaCyber Watermark")
        
        # إعداد النافذة لتكون شفافة وفوق الجميع
        self.root.attributes('-topmost', True)      # دائماً في المقدمة
        self.root.attributes('-alpha', args.opacity) # الشفافية
        self.root.attributes('-fullscreen', True)    # ملء الشاشة
        self.root.overrideredirect(True)             # بدون إطار
        
        # جعل خلفية النافذة شفافة تماماً
        # على macOS: استخدام لون خاص للشفافية
        bg_color = 'systemTransparent' if sys.platform == 'darwin' else 'black'
        self.root.configure(bg=bg_color)
        
        # جعل النافذة غير قابلة للنقر (click-through)
        # على macOS: NSWindow.ignoresMouseEvents = true
        try:
            self.root.wm_attributes('-transparent', True)
        except tk.TclError:
            pass

        # الحصول على أبعاد الشاشة
        self.screen_w = self.root.winfo_screenwidth()
        self.screen_h = self.root.winfo_screenheight()
        
        # Canvas شفاف
        self.canvas = tk.Canvas(
            self.root,
            width=self.screen_w,
            height=self.screen_h,
            bg=bg_color,
            highlightthickness=0
        )
        self.canvas.pack()
        
        # إعداد الخط
        self.font = tkfont.Font(
            family="Helvetica",
            size=args.font_size,
            weight="bold"
        )
        
        if args.scrolling:
            self._setup_scrolling_text()
        else:
            self._setup_static_text()
        
        # معالجة إشارة الإيقاف
        self.root.protocol("WM_DELETE_WINDOW", self.quit)
        
        # تشغيل حلقة الأحداث
        self.root.mainloop()
    
    def _get_position(self, text_width, text_height):
        """حساب موضع النص بناءً على الإعداد"""
        margin = 20
        pos = args.position.lower()
        
        if pos == "bottomright":
            return self.screen_w - text_width - margin, self.screen_h - text_height - margin
        elif pos == "bottomleft":
            return margin, self.screen_h - text_height - margin
        elif pos == "topright":
            return self.screen_w - text_width - margin, margin
        elif pos == "topleft":
            return margin, margin
        elif pos == "center":
            return self.screen_w // 2, self.screen_h // 2
        elif pos == "diagonal":
            return self.screen_w // 3, self.screen_h // 2
        else:
            return self.screen_w - text_width - margin, self.screen_h - text_height - margin
    
    def _setup_static_text(self):
        """عرض نص ثابت"""
        # تقدير حجم النص
        text_width  = len(args.text) * args.font_size * 0.6
        text_height = args.font_size * 1.5
        x, y = self._get_position(text_width, text_height)
        
        # ظل النص للوضوح
        self.canvas.create_text(
            x + 1, y + 1,
            text=args.text,
            font=self.font,
            fill="#000000",
            anchor="sw"
        )
        
        # النص الرئيسي
        self.canvas.create_text(
            x, y,
            text=args.text,
            font=self.font,
            fill=args.color,
            anchor="sw"
        )
    
    def _setup_scrolling_text(self):
        """عرض نص متحرك (ticker)"""
        self.scroll_x = self.screen_w
        self.text_id = self.canvas.create_text(
            self.scroll_x,
            self.screen_h - 30,
            text=args.text,
            font=self.font,
            fill=args.color,
            anchor="w"
        )
        self._animate_scroll()
    
    def _animate_scroll(self):
        """تحريك النص من اليمين إلى اليسار"""
        self.scroll_x -= 2
        text_width = len(args.text) * args.font_size * 0.6
        if self.scroll_x < -text_width:
            self.scroll_x = self.screen_w
        
        self.canvas.coords(self.text_id, self.scroll_x, self.screen_h - 30)
        self.root.after(50, self._animate_scroll)
    
    def quit(self):
        self.root.destroy()

# ─── تشغيل التطبيق ────────────────────────────────────────────────────
if __name__ == "__main__":
    try:
        WatermarkOverlay()
    except KeyboardInterrupt:
        sys.exit(0)
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)
