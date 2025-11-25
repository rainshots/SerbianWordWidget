using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Forms; // NotifyIcon, ContextMenuStrip
using System.Windows.Controls; // ContextMenu, MenuItem
using MessageBox = System.Windows.MessageBox;
using System.Diagnostics;
using Microsoft.VisualBasic;

namespace SerbianWordOfTheDay
{
    public partial class MainWindow : Window
    {
        private void ShowIntroDialog()
        {
            string introText =
                "Эта программа создана для неспешного изучения новых слов.\n" +
                "Просто периодически поглядывай в это окно и слова будут потихоньку откладываться в голове.\n\n" +
                "Перенеси окно со словом в любое место, оно будет всегда поверх всех окон.\n" +
                "Слово периодически меняется (раз в несколько часов). Это можно отключить в настройках (ПКМ по окну со словом).\n" +
                "Либо можно пропускать слова вручную (ПКМ → Пропустить слово).\n\n" +
                "Также можно добавить свои слова (ПКМ → Открыть файл со словами). После редактирования файла со словами нужно нажать Обновить слова\n" +
                "Изначально в файле слов cодержится 500 часто употребляемых слов.\n"+
                "Просмотренные слова (в файле со словами в конце помечаются \"1\") больше не будут показаны.\n\n" +
                "Предложения можно отправить автору в Telegram:\n" +
                "@KonstantinB93 (Константин Буров)";

            MessageBox.Show(
                introText,
                "О программе",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        private void AddWordMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Сербское слово
                string serb = Interaction.InputBox(
                    "Введите сербское слово:",
                    "Добавить слово",
                    ""
                ).Trim();

                if (string.IsNullOrWhiteSpace(serb))
                    return; // отмена или пусто — выходим

                // 2. Русский перевод
                string rus = Interaction.InputBox(
                    "Введите перевод на русский:",
                    "Добавить слово",
                    ""
                ).Trim();

                // 3. Английский перевод (можно оставить пустым)
                string eng = Interaction.InputBox(
                    "Введите перевод на английский (можно оставить пустым):",
                    "Добавить слово",
                    ""
                ).Trim();

                // 4. Добавляем новую запись в список
                var newEntry = new WordEntry(serb, rus, eng, false);
                _words.Add(newEntry);

                // 5. Сохраняем в words.txt (добавленный станет с shown=0)
                SaveWordsToTxt();

                // 6. Перечитываем слова и сбрасываем очередь
                ReloadWords();
            }
            catch
            {
                // тихо игнорируем любые странности
            }
        }
        private void ResetAllWordsProgress()
        {
            if (_words.Count == 0)
                return;

            // 1. Обнуляем флаг Shown у всех слов
            for (int i = 0; i < _words.Count; i++)
            {
                var w = _words[i];
                if (w.Shown)
                {
                    _words[i] = w with { Shown = false };
                }
            }

            // 2. Сохраняем обратно в words.txt
            SaveWordsToTxt();

            // 3. Сбрасываем состояние показа
            _state.RemainingIndices = new List<int>();
            _state.SkippedIndices = new List<int>();
            _state.CurrentIndex = 0;
            _state.LastWordChange = DateTime.Now;
            SaveState();

            // 4. Показываем первое доступное слово
            PickNextWord();
            ShowCurrentWord();
        }

        private void ResetAllWordsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ResetAllWordsProgress();
        }
        private void CopyCurrentWordToClipboard()
        {
            try
            {
                if (_words.Count == 0) return;
                if (_state.CurrentIndex < 0 || _state.CurrentIndex >= _words.Count) return;

                var word = _words[_state.CurrentIndex];
                System.Windows.Clipboard.SetText(word.Serbian);
            }
            catch
            {
                // если по какой-то причине не получилось — просто молча игнорируем
            }
        }

        private void CopyWordMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CopyCurrentWordToClipboard();
        }
        private void SaveWordsToTxt()
        {
            try
            {
                if (_words.Count == 0) return;

                using var writer = new StreamWriter(_wordsTxtPath, false, System.Text.Encoding.UTF8);
                writer.WriteLine("# Формат: serbian|russian|english|shown(0/1)");
                foreach (var w in _words)
                {
                    int flag = w.Shown ? 1 : 0;
                    writer.WriteLine($"{w.Serbian}|{w.Russian}|{w.English}|{flag}");
                }
            }
            catch
            {
                // не критично
            }
        }
        private void AutoStartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi)
                return;

            bool enabled = mi.IsChecked;
            _state.AutoStartEnabled = enabled;
            SaveState();
            UpdateStartupShortcut(enabled);
        }
        private void FrequencyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem clicked || clicked.Tag is not int option)
                return;

            // Снимаем галочки у соседей
            if (clicked.Parent is MenuItem parent)
            {
                foreach (var item in parent.Items.OfType<MenuItem>())
                {
                    item.IsChecked = item == clicked;
                }
            }

            _state.WordChangeOption = option;
            _state.LastWordChange = DateTime.Now; // отсчитываем интервал с текущего момента
            SaveState();
        }
        private void ReloadWords()
        {
            try
            {
                // Запоминаем текущее слово (по значениям, а не по индексу)
                string? currentSerb = null;
                string? currentRus = null;
                string? currentEng = null;

                if (_words.Count > 0 &&
                    _state.CurrentIndex >= 0 &&
                    _state.CurrentIndex < _words.Count)
                {
                    var cur = _words[_state.CurrentIndex];
                    currentSerb = cur.Serbian;
                    currentRus = cur.Russian;
                    currentEng = cur.English;
                }

                // Перечитываем words.txt / words.json / fallback
                LoadWords();

                // Обновляем очереди, но не переключаем слово
                _state.RemainingIndices = new List<int>();
                _state.SkippedIndices ??= new List<int>();

                if (_words.Count == 0)
                {
                    SerbianText.Text = "";
                    RussianText.Text = "";
                    EnglishText.Text = "";
                    SaveState();
                    return;
                }

                // Пытаемся найти в новом списке то же самое слово
                int foundIndex = -1;
                if (currentSerb != null)
                {
                    for (int i = 0; i < _words.Count; i++)
                    {
                        var w = _words[i];
                        if (string.Equals(w.Serbian, currentSerb, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(w.Russian, currentRus, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(w.English, currentEng, StringComparison.OrdinalIgnoreCase))
                        {
                            foundIndex = i;
                            break;
                        }
                    }
                }

                if (foundIndex >= 0)
                {
                    // Нашли то же слово — просто остаёмся на нём
                    _state.CurrentIndex = foundIndex;
                    ShowCurrentWord();
                }
                else
                {
                    // Текущего слова больше нет (или сильно изменилось) — берём следующее непросмотренное
                    _state.CurrentIndex = 0;
                    _state.LastWordChange = DateTime.Now;
                    PickNextWord();
                    ShowCurrentWord();
                }

                SaveState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось обновить слова:\n" + ex.Message,
                    "Serbian Word Widget",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        private void OpenWordsFile()
        {
            try
            {
                // если файла нет – создаем из текущего списка слов
                if (!File.Exists(_wordsTxtPath))
                {
                    SaveWordsToTxt();
                }

                var psi = new ProcessStartInfo
                {
                    FileName = _wordsTxtPath,
                    UseShellExecute = true
                };

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось открыть файл со словами:\n" + ex.Message,
                    "Serbian Word Widget",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        private void UpdateStartupShortcut(bool enabled)
        {
            try
            {
                string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string shortcutPath = Path.Combine(startupDir, "SerbianWordWidget.lnk");

                if (!enabled)
                {
                    if (File.Exists(shortcutPath))
                        File.Delete(shortcutPath);
                    return;
                }

                // enabled == true → создаём ярлык, если его нет
                if (File.Exists(shortcutPath))
                    return;

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    return;

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);

                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.WindowStyle = 1;
                shortcut.Description = "Serbian Word Widget";
                shortcut.Save();
            }
            catch
            {
                // тихо игнорируем
            }
        }
        private record WordEntry(
            [property: JsonPropertyName("serbian")] string Serbian,
            [property: JsonPropertyName("russian")] string Russian,
            [property: JsonPropertyName("english")] string English,
            [property: JsonPropertyName("shown")] bool Shown = false
        );

        private class AppState
        {
            public double Left { get; set; } = 10;
            public double Top { get; set; } = 10;
            public int CurrentIndex { get; set; } = 0;
            public DateTime LastWordChange { get; set; } = DateTime.MinValue;
            public List<int> RemainingIndices { get; set; } = new();
            public List<int> SkippedIndices { get; set; } = new();

            // 1..7 (1–6 = часы, 7 = "Не сменять")
            public int WordChangeOption { get; set; } = 3;

            // автозапуск
            public bool AutoStartEnabled { get; set; } = true;

            // Показано ли приветственное сообщение
            public bool IntroShown { get; set; } = false;
        }

        private readonly List<WordEntry> _fallbackWords = new()
        {
            new("polako", "медленно", "slowly"),
            new("gore", "вверх", "up"),
            new("dole", "вниз", "down"),
            new("napred", "вперед", "forward"),
            new("nazad", "назад", "back"),
            new("levo", "налево", "left"),
            new("desno", "направо", "right"),
            new("vera", "вера", "faith"),
            new("car", "царь", "emperor"),
            new("kralj", "король", "king"),
            new("vojnik", "солдат", "soldier"),
            new("profesija", "профессия", "profession"),
            new("umetnik", "художник/артист", "artist"),
            new("glumac", "актёр", "actor"),
            new("pevač", "певец", "singer"),
            new("pisac", "писатель", "writer"),
            new("slikar", "живописец", "painter"),
            new("inženjer", "инженер", "engineer"),
            new("programer", "программист", "programmer"),
            new("novinar", "журналист", "journalist"),
            new("sportista", "спортсмен", "athlete"),
            new("političar", "политик", "politician"),
            new("turista", "турист", "tourist"),
            new("blizu", "близко", "near"),
            new("daleko", "далеко", "far"),
            new("iznad", "над", "above"),
            new("ispod", "под", "under"),
            new("pre", "до", "before"),
            new("poraz", "поражение", "defeat"),
            new("pogrešno", "ошибочно", "wrongly"),
            new("posle", "после", "after"),
            new("preko", "через", "across/over"),
            new("pored", "рядом", "beside"),
            new("oko", "вокруг", "around"),
            new("sa", "с", "with"),
            new("bez", "без", "without"),
            new("među", "между", "between"),
            new("kuća", "дом", "house"),
            new("grad", "город", "city"),
            new("ulica", "улица", "street"),
            new("prijatelj", "друг", "friend"),
            new("porodica", "семья", "family"),
            new("vreme", "погода, время", "weather, time"),
            new("dan", "день", "day"),
            new("noć", "ночь", "night"),
            new("jutro", "утро", "morning"),
            new("veče", "вечер", "evening"),
            new("hleb", "хлеб", "bread"),
            new("voda", "вода", "water"),
            new("mleko", "молоко", "milk"),
            new("sto", "стол", "table"),
            new("stolica", "стул", "chair"),
            new("raditi", "работать/делать", "to work/do"),
            new("ići", "идти", "to go"),
            new("doći", "прийти", "to come"),
            new("videti", "видеть", "to see"),
            new("čuti", "слышать", "to hear"),
            new("jesti", "есть", "to eat"),
            new("piti", "пить", "to drink"),
            new("spavati", "спать", "to sleep"),
            new("voleti", "любить", "to love"),
            new("želeti", "желать/хотеть", "to want"),
            new("moći", "мочь", "can/to be able"),
            new("trebati", "нуждаться", "to need"),
            new("dati", "дать", "to give"),
            new("uzeti", "взять", "to take"),
            new("reći", "сказать", "to say"),
            new("govoriti", "говорить", "to speak"),
            new("znati", "знать", "to know"),
            new("učiti", "учить", "to learn"),
            new("pisati", "писать", "to write"),
            new("čitati", "читать", "to read"),
            new("stan", "квартира", "apartment"),
            new("škola", "школа", "school"),
            new("posao", "работа", "job/work"),
            new("auto", "машина", "car"),
            new("autobus", "автобус", "bus"),
            new("voz", "поезд", "train"),
            new("voda", "вода", "water"),
            new("hrana", "еда", "food"),
            new("kruh/hleb", "хлеб", "bread"),
            new("voće", "фрукты", "fruit"),
            new("povrće", "овощи", "vegetables"),
            new("kafa", "кофе", "coffee"),
            new("čaj", "чай", "tea"),
            new("dan", "день", "day"),
            new("noć", "ночь", "night"),
            new("sat", "час", "hour/clock"),
            new("jutro", "утро", "morning"),
            new("veče", "вечер", "evening"),
            new("godina", "год", "year"),
            new("nedelja", "неделя", "week/Sunday"),
            new("mesec", "месяц/луна", "month/moon"),
            new("čovek", "человек/мужчина", "man/person"),
            new("žena", "женщина", "woman"),
            new("dete", "ребёнок", "child"),
            new("prijatelj", "друг", "friend"),
            new("porodica", "семья", "family"),
            new("ime", "имя", "name"),
            new("glava", "голова", "head"),
            new("oko", "глаз", "eye"),
            new("ruka", "рука", "hand/arm"),
            new("noga", "нога", "leg"),
            new("srce", "сердце", "heart"),
            new("zdravlje", "здоровье", "health"),
            new("sunce", "солнце", "sun"),
            new("zemlja", "земля", "earth/ground"),
            new("vreme", "погода/время", "weather/time"),
            new("kuvar", "повар", "cook/chef"),
            new("učitelj", "учитель", "teacher"),
            new("doktor", "врач", "doctor"),
            new("prodavnica", "магазин", "shop"),
            new("pas", "собака", "dog"),
            new("mačka", "кошка", "cat"),
            new("ptica", "птица", "bird"),
            new("riba", "рыба", "fish"),
            new("kuvati", "готовить", "to cook"),
            new("voziti", "водить", "to drive"),
            new("igrati", "играть", "to play"),
            new("gledati", "смотреть", "to watch"),
            new("čekati", "ждать", "to wait"),
            new("trčati", "бежать", "to run"),
            new("hodati", "ходить", "to walk"),
            new("stajati", "стоять", "to stand"),
            new("sedeti", "сидеть", "to sit"),
            new("ležati", "лежать", "to lie down"),
            new("toplo", "тепло", "warm"),
            new("hladno", "холодно", "cold"),
            new("dobro", "хорошо", "good"),
            new("loše", "плохо", "bad"),
            new("veliki", "большой", "big"),
            new("mali", "маленький", "small"),
            new("novi", "новый", "new"),
            new("stari", "старый", "old"),
            new("lep", "красивый", "beautiful/nice"),
            new("ružan", "некрасивый", "ugly"),
            new("skup", "дорогой", "expensive"),
            new("jeftin", "дешёвый", "cheap"),
            new("ovde", "здесь", "here"),
            new("tamo", "там", "there"),
            new("zbogom", "пока/до свидания", "goodbye"),
            new("hvala", "спасибо", "thank you"),
            new("molim", "пожалуйста/прошу", "please/you're welcome"),
            new("izvinite", "извините", "sorry/excuse me"),
            new("jer", "потому что", "because"),
            new("ali", "но", "but"),
            new("ili", "или", "or"),
            new("ako", "если", "if"),
            new("kada", "когда", "when"),
            new("gde", "где", "where"),
            new("zašto", "почему", "why"),
            new("koliko", "сколько", "how much/many"),
            new("kako", "как", "how"),
            new("ovaj", "этот", "this (masc)"),
            new("ova", "эта", "this (fem)"),
            new("ovo", "это", "this (neut)"),
            new("taj", "тот (masc)", "that"),
            new("tamo", "там", "there"),
            new("ovde", "здесь", "here"),
            new("sada", "сейчас", "now"),
            new("jučе", "вчера", "yesterday"),
            new("sutra", "завтра", "tomorrow"),
            new("uvek", "всегда", "always"),
            new("nikad", "никогда", "never"),
            new("često", "часто", "often"),
            new("ponekad", "иногда", "sometimes"),
            new("retko", "редко", "rarely"),
            new("opet", "снова", "again"),
            new("odmah", "сразу", "immediately"),
            new("brzo", "быстро", "fast"),
            new("više", "больше", "more"),
            new("manje", "меньше", "less"),
            new("mnogo", "много", "a lot"),
            new("malo", "мало", "a little"),
            new("dosta", "достаточно", "enough"),
            new("previše", "слишком", "too much"),
            new("ovuda", "сюда", "this way"),
            new("tuda", "туда", "that way"),
            new("iz", "из", "from/out of"),
            new("u", "в", "in/into"),
            new("na", "на", "on/onto"),
            new("do", "до", "to/until"),
            new("za", "для/за", "for"),
            new("od", "от", "from"),
            new("po", "по", "along/by"),
            new("ka", "к", "toward"),
            new("moj", "мой", "my (masc)"),
            new("moja", "моя", "my (fem)"),
            new("moje", "моё", "my (neut)"),
            new("tvoj", "твой", "your (masc)"),
            new("tvoja", "твоя", "your (fem)"),
            new("tvoje", "твоё", "your (neut)"),
            new("naš", "наш", "our (masc)"),
            new("naša", "наша", "our (fem)"),
            new("naše", "наше", "our (neut)"),
            new("vaš", "ваш", "your (masc pl.)"),
            new("vaša", "ваша", "your (fem pl.)"),
            new("vaše", "ваше", "your (neut pl.)"),
            new("ovaj", "этот", "this"),
            new("taj", "тот", "that"),
            new("ne", "нет/не", "no/not"),
            new("možda", "может быть", "maybe"),
            new("naravno", "конечно", "of course"),
            new("tačno", "точно/верно", "exactly"),
            new("dobrodošli", "добро пожаловать", "welcome"),
            new("izvinite", "простите", "forgive me"),
            new("oprosti", "прости", "sorry"),
            new("čuvati", "хранить/беречь", "to keep"),
            new("pomoći", "помочь", "to help"),
            new("verovati", "верить", "to believe"),
            new("otvoriti", "открыть", "to open"),
            new("zatvoriti", "закрыть", "to close"),
            new("kupiti", "купить", "to buy"),
            new("platiti", "заплатить", "to pay"),
            new("nositi", "нести/носить", "to carry"),
            new("voziti", "водить", "to drive"),
            new("razumeti", "понимать", "to understand"),
            new("sećati se", "помнить", "to remember"),
            new("zaboraviti", "забыть", "to forget"),
            new("početak", "начало", "beginning"),
            new("kraj", "конец", "end"),
            new("pitanje", "вопрос", "question"),
            new("odgovor", "ответ", "answer"),
            new("istina", "правда", "truth"),
            new("laž", "ложь", "lie"),
            new("problem", "проблема", "problem"),
            new("rešenje", "решение", "solution"),
            new("svet", "мир/вселенная", "world"),
            new("život", "жизнь", "life"),
            new("ljubav", "любовь", "love"),
            new("mira", "покой", "peace"),
            new("bolnica", "больница", "hospital"),
            new("apotekа", "аптека", "pharmacy"),
            new("novac", "деньги", "money"),
            new("račun", "счёт", "bill/check"),
            new("cena", "цена", "price"),
            new("prodavac", "продавец", "seller"),
            new("kupac", "покупатель", "buyer"),
            new("radnja", "магазин", "shop"),
            new("tržiste", "рынок", "market"),
            new("plaža", "пляж", "beach"),
            new("more", "море", "sea"),
            new("reka", "река", "river"),
            new("jezero", "озеро", "lake"),
            new("planina", "гора", "mountain"),
            new("šuma", "лес", "forest"),
            new("polje", "поле", "field"),
            new("biljka", "растение", "plant"),
            new("drvo", "дерево", "tree"),
            new("cveće", "цветы", "flowers"),
            new("kiša", "дождь", "rain"),
            new("sneg", "снег", "snow"),
            new("vetar", "ветер", "wind"),
            new("oblak", "облако", "cloud"),
            new("zvuk", "звук", "sound"),
            new("miris", "запах", "smell"),
            new("glas", "голос", "voice"),
            new("reč", "слово", "word"),
            new("rečenica", "предложение", "sentence"),
            new("pismo", "письмо", "letter"),
            new("telefon", "телефон", "phone"),
            new("računar", "компьютер", "computer"),
            new("internet", "интернет", "internet"),
            new("slika", "картинка/фото", "picture"),
            new("muzika", "музыка", "music"),
            new("pesma", "песня", "song"),
            new("sport", "спорт", "sport"),
            new("igra", "игра", "game"),
            new("fudbal", "футбол", "football/soccer"),
            new("košarka", "баскетбол", "basketball"),
            new("tenis", "теннис", "tennis"),
            new("plivanje", "плавание", "swimming"),
            new("kupanje", "купание", "bathing"),
            new("rođendan", "день рождения", "birthday"),
            new("slavlje", "праздник", "celebration"),
            new("poklon", "подарок", "gift"),
            new("zabava", "вечеринка", "party"),
            new("putovanje", "путешествие", "travel"),
            new("pasoš", "паспорт", "passport"),
            new("karta", "билет/карта", "ticket/map"),
            new("hotel", "отель", "hotel"),
            new("soba", "комната", "room"),
            new("kljuc", "ключ", "key"),
            new("stol", "стол", "table"),
            new("stolica", "стул", "chair"),
            new("krevet", "кровать", "bed"),
            new("vrata", "дверь", "door"),
            new("prozor", "окно", "window"),
            new("zid", "стена", "wall"),
            new("pod", "пол", "floor"),
            new("tavan", "потолок", "ceiling"),
            new("kuhinja", "кухня", "kitchen"),
            new("kupatilo", "ванная", "bathroom"),
            new("toalet", "туалет", "toilet"),
            new("frižider", "холодильник", "fridge"),
            new("šporet", "плита", "stove"),
            new("tanјir", "тарелка", "plate"),
            new("čaša", "стакан", "glass"),
            new("kašika", "ложка", "spoon"),
            new("viljuška", "вилка", "fork"),
            new("nož", "нож", "knife"),
            new("čas", "урок", "lesson/hour"),
            new("časopis", "журнал", "magazine"),
            new("knjiga", "книга", "book"),
            new("strana", "страница/сторона", "page/side"),
            new("učionica", "класс", "classroom"),
            new("student", "студент", "student"),
            new("ispit", "экзамен", "exam"),
            new("odmor", "перерыв/отпуск", "break/holiday"),
            new("poseta", "визит", "visit"),
            new("gost", "гость", "guest"),
            new("gladan", "голодный", "hungry"),
            new("žedan", "жаждущий", "thirsty"),
            new("umoran", "уставший", "tired"),
            new("bolestan", "больной", "sick"),
            new("srećan", "счастливый", "happy"),
            new("tužan", "грустный", "sad"),
            new("ljut", "злой", "angry"),
            new("uplašen", "испуганный", "scared"),
            new("bogat", "богатый", "rich"),
            new("siromašan", "бедный", "poor"),
            new("pametan", "умный", "smart"),
            new("glup", "глупый", "stupid"),
            new("težak", "тяжёлый", "heavy/difficult"),
            new("lak", "лёгкий", "light/easy"),
            new("čist", "чистый", "clean"),
            new("prljav", "грязный", "dirty"),
            new("sladak", "сладкий", "sweet"),
            new("slan", "солёный", "salty"),
            new("gorak", "горький", "bitter"),
            new("kiselo", "кислый", "sour"),
            new("zanimljiv", "интересный", "interesting"),
            new("dosadan", "скучный", "boring"),
            new("važno", "важно", "important"),
            new("moguće", "возможно", "possible"),
            new("deo", "часть", "part"),
            new("deo dana", "часть дня", "part of the day"),
            new("moment", "момент", "moment"),
            new("sadržaj", "содержание", "content"),
            new("ideja", "идея", "idea"),
            new("plan", "план", "plan"),
            new("cilj", "цель", "goal"),
            new("pravilo", "правило", "rule"),
            new("zakon", "закон", "law"),
            new("država", "государство/страна", "state/country"),
            new("gradjanin", "гражданин", "citizen"),
            new("poštа", "почта", "post office"),
            new("adresa", "адрес", "address"),
            new("broj", "номер/число", "number"),
            new("količina", "количество", "quantity"),
            new("snaga", "сила", "strength"),
            new("brzina", "скорость", "speed"),
            new("visina", "высота", "height"),
            new("širina", "ширина", "width"),
            new("dubina", "глубина", "depth"),
            new("težina", "вес/трудность", "weight/difficulty"),
            new("izbor", "выбор", "choice"),
            new("pomoć", "помощь", "help"),
            new("savet", "совет", "advice"),
            new("iskustvo", "опыт", "experience"),
            new("znanje", "знание", "knowledge"),
            new("sećanje", "память", "memory"),
            new("navika", "привычка", "habit"),
            new("pažnja", "внимание", "attention"),
            new("pogled", "взгляд", "look"),
            new("pojam", "понятие", "concept"),
            new("telo", "тело", "body"),
            new("koža", "кожа", "skin"),
            new("kosa", "волосы", "hair"),
            new("lice", "лицо", "face"),
            new("nos", "нос", "nose"),
            new("usta", "рот", "mouth"),
            new("zubi", "зубы", "teeth"),
            new("leđa", "спина", "back"),
            new("stomak", "живот", "stomach"),
            new("glasno", "громко", "loud"),
            new("tiho", "тихо", "quiet"),
            new("sreća", "удача/счастье", "luck/happiness"),
            new("strah", "страх", "fear"),
            new("briga", "забота/беспокойство", "worry/care"),
            new("radost", "радость", "joy"),
            new("bol", "боль", "pain"),
            new("nada", "надежда", "hope"),
            new("želja", "желание", "wish"),
            new("šteta", "вред/жалко", "damage/shame"),
            new("opasno", "опасно", "dangerous"),
            new("sigurno", "безопасно/наверняка", "safe/sure"),
            new("zabranjeno", "запрещено", "forbidden"),
            new("dopušteno", "разрешено", "allowed"),
            new("tačno", "точно", "exact/precise"),
            new("netačno", "неверно", "incorrect"),
            new("pravo", "право/прямо", "right/straight"),
            new("krivо", "неправильно/криво", "wrong/crooked"),
            new("zadatak", "задание", "task"),
            new("dokaz", "доказательство", "proof"),
            new("primedba", "замечание", "remark"),
            new("problem", "проблема", "problem"),
            new("otkriće", "открытие", "discovery"),
            new("pitanje", "вопрос", "question"),
            new("stvar", "вещь", "thing"),
            new("predmet", "предмет/объект", "object"),
            new("kutija", "коробка", "box"),
            new("torba", "сумка", "bag"),
            new("papir", "бумага", "paper"),
            new("olovka", "карандаш", "pencil"),
            new("hemijska", "ручка", "pen"),
            new("radni sto", "рабочий стол", "desk"),
            new("sat vremena", "час времени", "hour (duration)"),
            new("slučaj", "случай", "case/event"),
            new("primer", "пример", "example"),
            new("priča", "рассказ/история", "story"),
            new("zadatak", "задание", "task"),
            new("znak", "знак", "sign"),
            new("točka/tačka", "точка", "dot/point"),
            new("komad", "кусок", "piece"),
            new("deo", "часть", "part"),
            new("gubitak", "потеря", "loss"),
            new("dobitak", "выигрыш/прибыль", "gain"),
            new("posao", "работа", "job/work"),
            new("firma", "компания", "company"),
            new("proizvod", "товар/продукт", "product"),
            new("usluga", "услуга", "service"),
            new("trening", "тренировка", "training"),
            new("vežba", "упражнение", "exercise"),
            new("navijač", "фанат", "fan"),
            new("takmičenje", "соревнование", "competition"),
            new("rezultat", "результат", "result"),
            new("pobeda", "победа", "victory"),
            new("tačno", "верно", "correctly"),
            new("zgodа", "случай/ситуация", "occasion"),
            new("red", "порядок/очередь", "order/queue"),
            new("haos", "хаос", "chaos"),
            new("početak", "начало", "start"),
            new("kraj", "конец", "end"),
            new("iznenađenje", "сюрприз", "surprise"),
            new("dogadjaj", "событие", "event"),
            new("pričati", "рассказывать", "to tell"),
            new("postojati", "существовать", "to exist"),
            new("živeti", "жить", "to live"),
            new("doručak", "завтрак", "breakfast"),
            new("ručak", "обед", "lunch"),
            new("večera", "ужин", "dinner"),
            new("obrok", "приём пищи", "meal"),
            new("glad", "голод", "hunger"),
            new("žeđ", "жажда", "thirst"),
            new("kuvarica", "повариха", "cook (fem)"),
            new("restoran", "ресторан", "restaurant"),
            new("meni", "меню", "menu"),
            new("sto", "стол", "table"),
            new("porcija", "порция", "portion"),
            new("račun", "счёт", "bill"),
            new("kartica", "карта (банковская)", "card"),
            new("keš", "наличные", "cash"),
            new("so", "соль", "salt"),
            new("biber", "перец", "pepper"),
            new("ulje", "масло", "oil"),
            new("šećer", "сахар", "sugar"),
            new("pirinač", "рис", "rice"),
            new("meso", "мясо", "meat"),
            new("pile", "курица", "chicken"),
            new("svinjetina", "свинина", "pork"),
            new("govedina", "говядина", "beef"),
            new("sir", "сыр", "cheese"),
            new("jaje", "яйцо", "egg"),
            new("maslac", "масло сливочное", "butter"),
            new("pita", "пирог", "pie"),
            new("čokolada", "шоколад", "chocolate"),
            new("sladoled", "мороженое", "ice cream"),
            new("kolač", "пирожное", "cake"),
            new("vožnja", "езда", "ride"),
            new("saobraćaj", "трафик/движение", "traffic"),
            new("semafor", "светофор", "traffic light"),
            new("put", "дорога/путь", "road/way"),
            new("ulaz", "вход", "entrance"),
            new("izlaz", "выход", "exit"),
            new("skretanje", "поворот", "turn"),
            new("parking", "парковка", "parking"),
            new("karte", "карты", "cards"),
            new("pomoćnik", "помощник", "assistant"),
            new("radnik", "рабочий", "worker"),
            new("šef", "шеф/начальник", "boss"),
            new("šalter", "окошко обслуживания", "service window"),
            new("dokument", "документ", "document"),
            new("identitet", "личность/ID", "identity"),
            new("institucija", "учреждение", "institution"),
            new("zgrada", "здание", "building"),
            new("ulaz", "вход", "entrance"),
            new("sprat", "этаж", "floor"),
            new("lift", "лифт", "elevator"),
            new("stepenice", "лестница", "stairs"),
            new("oružje", "оружие", "weapon"),
            new("policija", "полиция", "police"),
            new("opasnost", "опасность", "danger"),
            new("zaključati", "закрыть на замок", "to lock"),
            new("otključati", "открыть замок", "to unlock"),
            new("čuvati", "охранять", "to guard"),
            new("žuriti", "спешить", "to hurry"),
            new("organizovati", "организовать", "to organize"),
            new("kretati se", "двигаться", "to move"),
            new("smisliti", "придумать", "to invent"),
            new("graditi", "строить", "to build"),
            new("popraviti", "починить", "to repair"),
            new("rasti", "расти", "to grow"),
            new("kupovati", "покупать", "to buy"),
            new("prodati", "продать", "to sell"),
            new("slati", "посылать", "to send"),
            new("primiti", "получить", "to receive"),
            new("izabrati", "выбрать", "to choose"),
            new("odlučiti", "решить", "to decide"),
            new("pretpostaviti", "предположить", "to assume"),
            new("čitljiv", "читабельный", "readable"),
            new("pospan", "сонный", "sleepy"),
            new("živ", "живой", "alive"),
            new("mrtav", "мертвый", "dead"),
            new("ozbiljan", "серьёзный", "serious"),
            new("smešan", "смешной", "funny"),
            new("hrabar", "смелый", "brave"),
            new("lenj", "ленивый", "lazy"),
            new("brz", "быстрый", "fast"),
            new("spor", "медленный", "slow"),
            new("tanak", "тонкий", "thin"),
            new("debeo", "толстый", "thick/fat"),
            new("visok", "высокий", "tall/high"),
            new("nizak", "низкий", "short/low"),
            new("bog", "бог", "god"),
            new("duša", "душа", "soul"),
            new("molitva", "молитва", "prayer"),
            new("crkva", "церковь", "church"),
            new("praznik", "праздник (религ.)", "holiday"),
            
            new("kupac", "покупатель", "customer"),
            new("poznat", "известный", "famous"),
            new("važan", "важный", "important"),
            new("poseban", "особенный", "special"),
            new("običan", "обычный", "usual"),
            new("različit", "разный", "different"),
            new("isti", "тот же", "same"),
            // добавь свои дальше...
        };

        private List<WordEntry> _words = new();
        private readonly Random _random = new();

        private AppState _state = new();
        private readonly DispatcherTimer _timer;

        private NotifyIcon? _notifyIcon;


        // Пути для файлов
        private readonly string _stateFilePath;
        private readonly string _wordsJsonPath;
        private readonly string _wordsTxtPath;

        public MainWindow()
        {
            InitializeComponent();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string stateFolder = Path.Combine(appData, "SerbianWordWidget");
            Directory.CreateDirectory(stateFolder);
            _stateFilePath = Path.Combine(stateFolder, "state.json");

            string exeDir = AppContext.BaseDirectory;
            _wordsJsonPath = Path.Combine(exeDir, "words.json");
            _wordsTxtPath = Path.Combine(exeDir, "words.txt");

            LoadWords();
            LoadState();

            UpdateStartupShortcut(_state.AutoStartEnabled);

            Left = _state.Left;
            Top = _state.Top;

            Topmost = true;

            // Таймер ...
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            InitNotifyIcon();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ShowCurrentWord();

            if (!_state.IntroShown)
            {
                ShowIntroDialog();
                _state.IntroShown = true;
                SaveState();
            }
        }

        // ================== Загрузка слов ==================

        private void LoadWords()
        {
            try
            {
                // 1) Пытаемся прочитать words.txt
                if (File.Exists(_wordsTxtPath))
                {
                    var list = new List<WordEntry>();
                    foreach (var line in File.ReadAllLines(_wordsTxtPath))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;
                        if (trimmed.StartsWith("#")) continue;

                        var parts = trimmed.Split('|');
                        if (parts.Length >= 1)
                        {
                            string serb = parts[0].Trim();
                            string rus = parts.Length > 1 ? parts[1].Trim() : "";
                            string eng = parts.Length > 2 ? parts[2].Trim() : "";

                            bool shown = false;
                            if (parts.Length > 3)
                            {
                                var flag = parts[3].Trim();
                                shown = flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase);
                            }

                            list.Add(new WordEntry(serb, rus, eng, shown));
                        }
                    }

                    if (list.Count > 0)
                    {
                        _words = list;
                        return;
                    }
                }

                // 2) Потом пробуем words.json
                if (File.Exists(_wordsJsonPath))
                {
                    string json = File.ReadAllText(_wordsJsonPath);
                    List<WordEntry>? loaded = JsonSerializer.Deserialize<List<WordEntry>>(json);
                    if (loaded != null && loaded.Count > 0)
                    {
                        _words = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки списка слов: " + ex.Message,
                    "Serbian Word Widget", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 3) Фоллбек — встроенный список
            _words = _fallbackWords;
        }

        // ================== Загрузка/сохранение состояния ==================

        private void LoadState()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    string json = File.ReadAllText(_stateFilePath);
                    AppState? state = JsonSerializer.Deserialize<AppState>(json);
                    if (state != null)
                    {
                        _state = state;

                        _state.RemainingIndices ??= new List<int>();
                        _state.SkippedIndices ??= new List<int>();

                        // чистим индексы, которые выходят за границы после смены словаря
                        _state.RemainingIndices = _state.RemainingIndices
                            .Where(i => i >= 0 && i < _words.Count)
                            .Distinct()
                            .ToList();

                        _state.SkippedIndices = _state.SkippedIndices
                            .Where(i => i >= 0 && i < _words.Count)
                            .Distinct()
                            .ToList();

                        if (_state.CurrentIndex < 0 || _state.CurrentIndex >= _words.Count)
                        {
                            _state.CurrentIndex = 0;
                        }

                        return;
                    }
                }
            }
            catch
            {
                // если не получилось — просто сбросим состояние
            }

            _state = new AppState
            {
                Left = 10,
                Top = 10,
                CurrentIndex = 0,
                LastWordChange = DateTime.MinValue,
                RemainingIndices = new List<int>(),
                SkippedIndices = new List<int>()
            };
        }

        private void SaveState()
        {
            try
            {
                _state.Left = Left;
                _state.Top = Top;
                string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_stateFilePath, json);
            }
            catch
            {
                // не критично
            }
        }

        // ================== Логика выбора слова ==================

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // смотрим, включена ли автосмена
            double? intervalHours = GetIntervalHoursFromOption(_state.WordChangeOption);
            if (intervalHours == null)
            {
                // "Не сменять" — ничего не делаем
                return;
            }

            var interval = TimeSpan.FromHours(intervalHours.Value);

            if (DateTime.Now - _state.LastWordChange >= interval)
            {
                PickNextWord();
                ShowCurrentWord();
                SaveState();
            }
        }

        private double? GetIntervalHoursFromOption(int option)
        {
            return option switch
            {
                1 => 1.0,
                2 => 2.0,
                3 => 3.0,
                4 => 4.0,
                5 => 5.0,
                6 => 6.0,
                _ => null // 7 и всё остальное — "Не сменять"
            };
        }

        private void EnsureRemainingIndices()
        {
            _state.RemainingIndices ??= new List<int>();
            _state.SkippedIndices ??= new List<int>();

            // Список индексов только из НЕПОКАЗАННЫХ слов (Shown == false)
            // и не находящихся в SkippedIndices (на всякий случай)
            var indices = Enumerable.Range(0, _words.Count)
                .Where(i => !_state.SkippedIndices.Contains(i) && !_words[i].Shown)
                .ToList();

            // ВАЖНО: больше НЕ перемешиваем — порядок как в файле (0,1,2,...)
            _state.RemainingIndices = indices;
        }

        private void PickNextWord()
        {
            if (_words.Count == 0) return;

            EnsureRemainingIndices();
            if (_state.RemainingIndices.Count == 0)
                return; // новых слов нет

            int nextIndex = _state.RemainingIndices[0];
            _state.RemainingIndices.RemoveAt(0);

            _state.CurrentIndex = nextIndex;
            _state.LastWordChange = DateTime.Now;

            // помечаем слово как показанное и сохраняем в words.txt
            var w = _words[nextIndex];
            if (!w.Shown)
            {
                _words[nextIndex] = w with { Shown = true };
                if (File.Exists(_wordsTxtPath))
                {
                    SaveWordsToTxt();
                }
            }
        }

        private void ShowCurrentWord()
        {
            if (_words.Count == 0) return;

            if (_state.CurrentIndex < 0 || _state.CurrentIndex >= _words.Count)
            {
                _state.CurrentIndex = 0;
            }

            var word = _words[_state.CurrentIndex];

            SerbianText.Text = word.Serbian;
            RussianText.Text = word.Russian;
            EnglishText.Text = word.English;
        }

        // Пометить текущее слово как "пропущенное" (известное) и взять следующее
        private void SkipCurrentWord()
        {
            if (_words.Count == 0) return;

            _state.SkippedIndices ??= new List<int>();
            _state.RemainingIndices ??= new List<int>();

            if (!_state.SkippedIndices.Contains(_state.CurrentIndex))
            {
                _state.SkippedIndices.Add(_state.CurrentIndex);
            }

            _state.RemainingIndices.RemoveAll(i => i == _state.CurrentIndex);

            // если отмечены все слова — сбрасываем "известные"
            if (_state.SkippedIndices.Count >= _words.Count)
            {
                _state.SkippedIndices.Clear();
                _state.RemainingIndices.Clear();
            }

            PickNextWord();
            ShowCurrentWord();
            SaveState();
        }

        // ================== Перетаскивание и ПКМ по окну ==================

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // иногда кидает исключение при DragMove — игнорируем
                }
            }
        }

        // ПКМ по виджету — показываем контекстное меню
        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            // --- Верх: основные действия ---
            var skipItem = new MenuItem { Header = "Пропустить слово" };
            skipItem.Click += (_, _) => SkipCurrentWord();

            var copyItem = new MenuItem { Header = "Скопировать слово" };
            copyItem.Click += CopyWordMenuItem_Click;

            var openWordsItem = new MenuItem { Header = "Открыть файл со словами" };
            openWordsItem.Click += (_, _) => OpenWordsFile();

            var reloadItem = new MenuItem { Header = "Обновить слова" };
            reloadItem.Click += (_, _) => ReloadWords();

            // --- Настройки ---
            var settingsItem = new MenuItem { Header = "Настройки" };

            var freqItem = new MenuItem { Header = "Частота смены слова" };
            void AddFreqOption(string text, int option)
            {
                var mi = new MenuItem
                {
                    Header = text,
                    IsCheckable = true,
                    IsChecked = _state.WordChangeOption == option,
                    Tag = option
                };
                mi.Click += FrequencyMenuItem_Click;
                freqItem.Items.Add(mi);
            }
            var addWordItem = new MenuItem { Header = "Добавить слово" };
            addWordItem.Click += AddWordMenuItem_Click;

            AddFreqOption("Каждый час", 1);
            AddFreqOption("Каждые 2 часа", 2);
            AddFreqOption("Каждые 3 часа", 3);
            AddFreqOption("Каждые 4 часа", 4);
            AddFreqOption("Каждые 5 часов", 5);
            AddFreqOption("Каждые 6 часов", 6);
            AddFreqOption("Не сменять", 7);

            var autoStartItem = new MenuItem
            {
                Header = "Открывать при запуске Windows",
                IsCheckable = true,
                IsChecked = _state.AutoStartEnabled
            };
            autoStartItem.Click += AutoStartMenuItem_Click;
            // Новый пункт: сброс всех слов
            var resetWordsItem = new MenuItem { Header = "Сделать все слова непросмотренными" };
            resetWordsItem.Click += ResetAllWordsMenuItem_Click;


            // наполняем "Настройки"
            settingsItem.Items.Add(freqItem);
            settingsItem.Items.Add(new Separator());
            settingsItem.Items.Add(autoStartItem);
            settingsItem.Items.Add(new Separator());
            settingsItem.Items.Add(resetWordsItem);

            // --- Выход ---
            var exitItem = new MenuItem { Header = "Закрыть" };
            exitItem.Click += (_, _) => Close();

            // --- Собираем меню (копировать — на верхнем уровне) ---
            menu.Items.Add(skipItem);
            menu.Items.Add(copyItem);
            menu.Items.Add(addWordItem);
            menu.Items.Add(openWordsItem);
            menu.Items.Add(reloadItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsItem);

            // новый пункт "О программе" ниже настроек
            var aboutItem = new MenuItem { Header = "О программе" };
            aboutItem.Click += (_, _) => ShowIntroDialog();
            menu.Items.Add(aboutItem);

            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _timer.Stop();
            SaveState();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }

        // ================== Иконка в трее ==================

        private void InitNotifyIcon()
        {
            try
            {
                _notifyIcon = new NotifyIcon
                {
                    Visible = true,
                    Text = "Serbian Word Widget"
                };

                string exeDir = AppContext.BaseDirectory;
                string iconPath = Path.Combine(exeDir, "app.ico");
                if (File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }

                var menu = new ContextMenuStrip();

                var showItem = new ToolStripMenuItem("Показать");
                showItem.Click += (_, _) => Dispatcher.Invoke(ShowWindowFromTray);

                var skipItem = new ToolStripMenuItem("Пропустить слово");
                skipItem.Click += (_, _) => Dispatcher.Invoke(SkipCurrentWord);

                var exitItem = new ToolStripMenuItem("Выход");
                exitItem.Click += (_, _) => Dispatcher.Invoke(Close);

                var openWordsTrayItem = new ToolStripMenuItem("Открыть файл со словами");
                openWordsTrayItem.Click += (_, _) => Dispatcher.Invoke(OpenWordsFile);

                var reloadTrayItem = new ToolStripMenuItem("Обновить слова");
                reloadTrayItem.Click += (_, _) => Dispatcher.Invoke(ReloadWords);

                menu.Items.Add(showItem);
                menu.Items.Add(skipItem);
                menu.Items.Add(openWordsTrayItem);
                menu.Items.Add(reloadTrayItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(exitItem);


                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.MouseClick += NotifyIcon_MouseClick;
            }
            catch
            {
                // если что-то пойдёт не так — живём без трея
            }
        }

        private void NotifyIcon_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(ShowWindowFromTray);
            }
        }

        private void ShowWindowFromTray()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Topmost = true;
        }
    }
}
