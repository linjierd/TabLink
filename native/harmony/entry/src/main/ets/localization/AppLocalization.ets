import { common } from '@kit.AbilityKit';
import { preferences } from '@kit.ArkData';
import { i18n } from '@kit.LocalizationKit';

export type AppLanguage = 'system' | 'zhHans' | 'english';
export const LANGUAGE_SYSTEM: AppLanguage = 'system';
export const LANGUAGE_ZH_HANS: AppLanguage = 'zhHans';
export const LANGUAGE_ENGLISH: AppLanguage = 'english';

const LANGUAGE_PREFERENCES_NAME: string = 'ui-language';
const LANGUAGE_PREFERENCE_KEY: string = 'app-language-v1';

class LanguagePreferenceQueue {
  private tail: Promise<void> = Promise.resolve();
  private sequence: number = 0;

  runExclusive<T>(operation: () => Promise<T>): Promise<T> {
    const work: Promise<T> = this.tail.then(operation);
    this.tail = work.then(() => {}, () => {});
    return work;
  }

  nextSequence(): number { return ++this.sequence; }
  isLatest(sequence: number): boolean { return sequence === this.sequence; }
}

const LANGUAGE_WRITES: LanguagePreferenceQueue = new LanguagePreferenceQueue();

export function parseStoredAppLanguage(value: unknown, exists: boolean): AppLanguage {
  if (!exists) return LANGUAGE_SYSTEM;
  if (value === LANGUAGE_SYSTEM || value === LANGUAGE_ZH_HANS || value === LANGUAGE_ENGLISH) return value as AppLanguage;
  return LANGUAGE_SYSTEM;
}

export class AppLocalization {
  private context: common.UIAbilityContext;
  private language: AppLanguage = LANGUAGE_SYSTEM;
  private revision: number = 0;

  constructor(context: common.UIAbilityContext) { this.context = context; }

  currentLanguage(): AppLanguage { return this.language; }

  async initialize(): Promise<AppLanguage> {
    const revision: number = ++this.revision;
    const selected = await LANGUAGE_WRITES.runExclusive(async (): Promise<AppLanguage> => {
      const store = await preferences.getPreferences(this.context, { name: LANGUAGE_PREFERENCES_NAME });
      const exists = await store.has(LANGUAGE_PREFERENCE_KEY);
      const stored = exists ? await store.get(LANGUAGE_PREFERENCE_KEY, '') : '';
      const parsed = parseStoredAppLanguage(stored, exists);
      if (exists && stored !== parsed) {
        await store.put(LANGUAGE_PREFERENCE_KEY, LANGUAGE_SYSTEM);
        await store.flush();
      }
      return parsed;
    });
    if (revision !== this.revision) return this.language;
    this.language = selected;
    this.applyPreferredLanguage(selected);
    return selected;
  }

  async setLanguage(requested: AppLanguage): Promise<AppLanguage> {
    const selected: AppLanguage = parseStoredAppLanguage(requested, true);
    const revision: number = ++this.revision;
    const sequence: number = LANGUAGE_WRITES.nextSequence();
    const saved = await LANGUAGE_WRITES.runExclusive(async (): Promise<boolean> => {
      try {
        const store = await preferences.getPreferences(this.context, { name: LANGUAGE_PREFERENCES_NAME });
        await store.put(LANGUAGE_PREFERENCE_KEY, selected);
        await store.flush();
        return true;
      } catch (_error) { return false; }
    });
    if (revision !== this.revision || !LANGUAGE_WRITES.isLatest(sequence)) return this.language;
    this.language = saved ? selected : LANGUAGE_SYSTEM;
    this.applyPreferredLanguage(this.language);
    return this.language;
  }

  refreshSystemLanguage(): void {
    if (this.language === LANGUAGE_SYSTEM) this.applyPreferredLanguage(LANGUAGE_SYSTEM);
  }

  text(name: string, arguments_: string[] = []): string {
    let value: string;
    try { value = this.context.resourceManager.getStringByNameSync(name); }
    catch (_error) { return name; }
    for (let index = 0; index < arguments_.length; index++) {
      value = value.split(`{${index}}`).join(arguments_[index]);
    }
    return value;
  }

  hostStatusText(name: string, message: string): string {
    const safe: string = message.replace(/[\u0000-\u001f\u007f-\u009f]/g, ' ').trim().substring(0, 180);
    const paused: boolean = name === 'session_capture_paused_remote';
    if (safe.length === 0 || /[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]/.test(safe) !== this.isSimplifiedChinese()) {
      return this.text(paused ? 'session_capture_paused_generic' : 'session_host_status_generic');
    }
    return this.text(name, [safe]);
  }

  private isSimplifiedChinese(): boolean {
    return this.preferredLanguage(this.language) === 'zh-CN';
  }

  private preferredLanguage(language: AppLanguage): string {
    const systemLanguage: string = i18n.System.getSystemLanguage().toLowerCase();
    return language === LANGUAGE_ZH_HANS ? 'zh-CN' :
      language === LANGUAGE_ENGLISH ? 'en' : systemLanguage.startsWith('zh') ? 'zh-CN' : 'en';
  }

  private applyPreferredLanguage(language: AppLanguage): void {
    // System mode deliberately maps every zh-* locale to Simplified Chinese;
    // every other locale uses English. This avoids vendor-specific fallback
    // differences for zh-HK/zh-TW while retaining the persisted system mode.
    const preferred: string = this.preferredLanguage(language);
    i18n.System.setAppPreferredLanguage(preferred);
  }
}
