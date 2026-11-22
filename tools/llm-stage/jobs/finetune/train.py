"""Fine-tune job on the Colab VM (tools/llm-stage): LoRA fine-tune of Gemma 4 (Unsloth) as the legal-stage reader, then a Q8_0 GGUF for Ollama.
usage: train.py <hf model>   (run from the job folder: data/train.jsonl; writes lora/, gguf/, gguf.txt, log.txt)
environment (run.sh exports job.env): MAXLEN (default 16384), MAX_STEPS (default -1: one epoch).
data/train.jsonl: {"messages": [user, assistant]}; the user turn is the runtime prompt (stage-prompt.txt, one person),
the assistant turn the answer JSON. Loss on the assistant turn only (train_on_responses_only, verified on the first
encoded examples: the unmasked tokens decode to exactly the answer JSON plus end-of-turn markup). Examples longer than
MAXLEN tokens are dropped and counted; more than 5 % dropped -> fail. log.txt carries counts, steps, losses, file names
and sizes only (never example text). Any failure: fail.txt with the reason, exit 2."""
import glob, json, os, re, sys, threading, time

MAXLEN = int(os.environ.get("MAXLEN", "16384"))
MAX_STEPS = int(os.environ.get("MAX_STEPS", "-1"))
MAX_DROP = 0.05
CHECK = 20  # encoded examples whose labels are verified


def log(msg):
    with open("log.txt", "a", encoding="utf-8") as f: f.write(time.strftime("%H:%M:%S ") + msg + "\n")


def fail(msg):
    log("FAIL " + msg)
    with open("fail.txt", "w", encoding="utf-8") as f: f.write(msg + "\n")
    sys.exit(2)


def pct(xs, q): return sorted(xs)[min(len(xs) - 1, int(q * len(xs)))]


def main():
    model_name = sys.argv[1]
    from unsloth import FastModel, is_bf16_supported
    from unsloth.chat_templates import get_chat_template, train_on_responses_only
    import torch
    from datasets import Dataset
    from transformers import TrainerCallback
    from trl import SFTConfig, SFTTrainer

    bf16 = bool(is_bf16_supported())
    log("train start %s gpu %s bf16 %s maxlen %d max_steps %d" % (model_name, torch.cuda.get_device_name(0), bf16, MAXLEN, MAX_STEPS))
    model, tokenizer = FastModel.from_pretrained(model_name=model_name, dtype=None, max_seq_length=MAXLEN, load_in_4bit=True, full_finetuning=False)
    log("model loaded, gpu memory reserved %.1f GB" % (torch.cuda.memory_reserved() / 1e9))
    model = FastModel.get_peft_model(model, finetune_vision_layers=False, finetune_language_layers=True, finetune_attention_modules=True,
                                     finetune_mlp_modules=True, r=16, lora_alpha=16, lora_dropout=0, bias="none", random_state=3407,
                                     use_gradient_checkpointing="unsloth")
    tokenizer = get_chat_template(tokenizer, chat_template="gemma-4")
    tok = getattr(tokenizer, "tokenizer", tokenizer)  # the text tokenizer inside a processor

    rows = [json.loads(l) for l in open("data/train.jsonl", encoding="utf-8")]
    def render(msgs): return tokenizer.apply_chat_template(msgs, tokenize=False)
    m0 = rows[0]["messages"]; t0 = render(m0)
    verbatim = lambda t: all(m["content"].strip() in t for m in m0)  # templates may trim the outer whitespace
    as_list = not verbatim(t0)
    if as_list:  # a processor template may want content parts
        conv = lambda ms: [{"role": m["role"], "content": [{"type": "text", "text": m["content"]}]} for m in ms]
        t0 = render(conv(m0))
        if not verbatim(t0): fail("chat template does not render the user and assistant content verbatim")
    else: conv = lambda ms: ms
    log("chat template: content as %s; turn markers user %s model %s" % ("parts" if as_list else "string", "<|turn>user\n" in t0, "<|turn>model\n" in t0))
    if "<|turn>user\n" not in t0 or "<|turn>model\n" not in t0: fail("rendered example lacks the <|turn>user / <|turn>model markers")
    bos = tok.bos_token
    strip_bos = bool(bos) and t0.startswith(bos) and tok("a").input_ids[:1] == [tok.bos_token_id]
    texts = [render(conv(r["messages"])) for r in rows]
    if strip_bos: texts = [t[len(bos):] if t.startswith(bos) else t for t in texts]
    log("leading bos stripped (the tokenizer adds it): %s" % strip_bos)
    lens = [len(tok(t).input_ids) for t in texts]
    keep = [i for i, n in enumerate(lens) if n <= MAXLEN]; dropped = len(rows) - len(keep)
    log("tokens per example p50 %d p90 %d p95 %d p99 %d max %d | over %d: %d of %d dropped (%.3f) | over 16384: %d"
        % (pct(lens, .5), pct(lens, .9), pct(lens, .95), pct(lens, .99), max(lens), MAXLEN, dropped, len(rows), dropped / len(rows), sum(n > 16384 for n in lens)))
    if dropped > MAX_DROP * len(rows): fail("%d of %d examples longer than %d tokens (more than 5 %%)" % (dropped, len(rows), MAXLEN))
    answers = [rows[i]["messages"][1]["content"] for i in keep]
    ds = Dataset.from_dict({"text": [texts[i] for i in keep]})

    cfg = SFTConfig(output_dir="ckpt", dataset_text_field="text", max_length=MAXLEN, packing=False, num_train_epochs=1, max_steps=MAX_STEPS,
                    per_device_train_batch_size=1, gradient_accumulation_steps=8, learning_rate=1e-4, warmup_ratio=0.03,
                    lr_scheduler_type="cosine", logging_steps=10, seed=3407, optim="adamw_8bit", fp16=not bf16, bf16=bf16,
                    save_strategy="no", report_to="none", dataset_num_proc=2)

    class Progress(TrainerCallback):
        def on_log(self, args, state, control, logs=None, **kw):
            l = logs or {}
            log("step %d/%d loss %s lr %s" % (state.global_step, state.max_steps, l.get("loss", l.get("train_loss", "-")), l.get("learning_rate", "-")))

    trainer = SFTTrainer(model=model, processing_class=tokenizer, train_dataset=ds, args=cfg, callbacks=[Progress()])
    trainer = train_on_responses_only(trainer, instruction_part="<|turn>user\n", response_part="<|turn>model\n")
    tds = trainer.train_dataset
    if len(tds) != len(keep): fail("train dataset has %d rows, expected %d" % (len(tds), len(keep)))
    longest = max(len(x) for x in tds["input_ids"])
    if longest > MAXLEN: fail("an encoded example has %d tokens > %d" % (longest, MAXLEN))
    markup = re.compile(r"<[^<>\s]*>|\s")
    for k in range(min(CHECK, len(tds))):
        ids, labels = tds[k]["input_ids"], tds[k]["labels"]
        on = [j for j, l in enumerate(labels) if l != -100]
        if not on or on != list(range(on[0], on[-1] + 1)) or any(labels[j] != ids[j] for j in on):
            fail("example %d: unmasked labels are not one contiguous copy of the input ids (%d unmasked)" % (k, len(on)))
        got = tok.decode([ids[j] for j in on])
        # The 12B template under current transformers may open the model turn with an empty thought channel before the
        # answer: allowed are the answer once, and around it only markup, whitespace or an empty "thought" channel.
        at = got.find(answers[k])
        rest = markup.sub("", got[:at] + got[at + len(answers[k]):]) if at >= 0 else None
        if k == 0: log("labels check example 0: unmasked tokens %d, answer occurrences %d, characters outside the answer and markup %d"
                       % (len(on), got.count(answers[k]), -1 if rest is None else len(rest)))  # counts only, never text
        if at < 0 or got.count(answers[k]) != 1 or rest not in ("", "thought"):
            fail("example %d: unmasked tokens are not the answer JSON plus end-of-turn markup (%d unmasked, %d masked)" % (k, len(on), len(ids) - len(on)))
        if k == 0: log("labels check example 0: tokens %d masked %d unmasked %d (answer JSON + %d chars of end markup)" % (len(ids), len(ids) - len(on), len(on), len(got) - len(answers[k])))
    log("labels check ok on %d examples" % min(CHECK, len(tds)))

    t = time.time(); st = trainer.train()
    log("train done in %.0fs, train loss %.4f, steps %d" % (time.time() - t, st.training_loss, st.global_step))
    model.save_pretrained("lora"); tokenizer.save_pretrained("lora")
    log("saved lora/: %s" % " ".join("%s %d" % (os.path.basename(f), os.path.getsize(f)) for f in sorted(glob.glob("lora/*"))))

    # the export merges, builds llama.cpp, converts and quantizes without writing log lines: a heartbeat for up to
    # 90 minutes keeps the runner's stall rule (no new log line for 45 minutes) from firing on a working export
    stop = threading.Event(); t = time.time()
    def beat():
        while not stop.wait(300) and time.time() - t < 5400: log("gguf export running %.0fs" % (time.time() - t))
    threading.Thread(target=beat, daemon=True).start()
    try: model.save_pretrained_gguf("gguf", tokenizer, quantization_method="Q8_0")
    except Exception as e: stop.set(); fail("save_pretrained_gguf failed: %s %s" % (type(e).__name__, str(e)[:200]))
    stop.set()
    big = [f for f in glob.glob("**/*.gguf", recursive=True) if os.path.getsize(f) >= 1e8]  # llama.cpp's own test vocabularies are smaller
    log("gguf export done in %.0fs: %s" % (time.time() - t, " ".join("%s %d" % (f, os.path.getsize(f)) for f in big) or "no .gguf of 100 MB or more"))
    found = sorted((f for f in big if "mmproj" not in os.path.basename(f).lower() and "q8_0" in os.path.basename(f).lower()), key=os.path.getsize)
    if not found or os.path.getsize(found[-1]) < 1e9: fail("no Q8_0 text .gguf of 1 GB or more after save_pretrained_gguf")
    with open("gguf.txt", "w", encoding="utf-8") as f: f.write(os.path.abspath(found[-1]) + "\n")
    log("TRAIN_DONE gguf %s %.2f GB" % (found[-1], os.path.getsize(found[-1]) / 1e9))


if __name__ == "__main__":
    try: main()
    except SystemExit: raise
    except BaseException as e:
        fail("train.py: %s %s" % (type(e).__name__, str(e)[:200]))
