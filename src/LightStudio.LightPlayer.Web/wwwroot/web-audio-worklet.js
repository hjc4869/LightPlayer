class LightPlayerPcmProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.channelCount = options.processorOptions.channelCount;
        this.queue = [];
        this.queuedSamples = 0;
        this.frameOffset = 0;
        this.active = false;
        this.sourceEnded = false;
        this.starved = false;
        this.generation = 0;
        this.port.onmessage = event => this.handleMessage(event.data);
    }

    handleMessage(message) {
        try { this.applyMessage(message); }
        catch (error) {
            this.port.postMessage({ type: "ack", id: message.id, generation: message.generation, error: error.stack || String(error) });
            return;
        }
        if (message.id) this.port.postMessage({ type: "ack", id: message.id, generation: this.generation });
    }

    applyMessage(message) {
        if (message.type === "reset") {
            this.generation = message.generation;
            this.queue.length = 0;
            this.queuedSamples = 0;
            this.frameOffset = 0;
            this.active = false;
            this.sourceEnded = true;
            this.starved = false;
            return;
        }

        if (message.generation !== this.generation) {
            return;
        }

        switch (message.type) {
            case "queue":
                if (this.queue.length >= 256 || this.queuedSamples + message.samples.length > 2 * 1024 * 1024)
                    throw new Error("WebAudio queue budget exceeded.");
                this.queuedSamples += message.samples.length;
                this.queue.push({
                    samples: message.samples,
                    frameCount: message.frameCount,
                });
                this.sourceEnded = false;
                this.starved = false;
                break;
            case "active":
                this.active = message.value;
                this.starved = false;
                break;
            case "sourceEnded":
                this.sourceEnded = message.value;
                break;
        }
    }

    process(_inputs, outputs) {
        const output = outputs[0];
        if (!this.active || output.length === 0) {
            return true;
        }

        const outputFrames = output[0].length;
        let outputOffset = 0;
        let consumedFrames = 0;

        while (outputOffset < outputFrames && this.queue.length > 0) {
            const chunk = this.queue[0];
            const availableFrames = chunk.frameCount - this.frameOffset;
            const framesToCopy = Math.min(availableFrames, outputFrames - outputOffset);

            for (let channel = 0; channel < output.length; channel++) {
                const sourceChannel = Math.min(channel, this.channelCount - 1);
                const destination = output[channel];
                for (let frame = 0; frame < framesToCopy; frame++) {
                    const sourceFrame = this.frameOffset + frame;
                    destination[outputOffset + frame] =
                        chunk.samples[sourceFrame * this.channelCount + sourceChannel];
                }
            }

            this.frameOffset += framesToCopy;
            outputOffset += framesToCopy;
            consumedFrames += framesToCopy;

            if (this.frameOffset === chunk.frameCount) {
                this.queue.shift();
                this.queuedSamples -= chunk.samples.length;
                this.frameOffset = 0;
            }
        }

        if (consumedFrames > 0) {
            this.port.postMessage({
                type: "consumed",
                frames: consumedFrames,
                // Graph time at which the last of these frames leaves the worklet. The output
                // device plays it one outputLatency later, which is what the position is measured
                // against.
                until: currentTime + (consumedFrames / sampleRate),
                generation: this.generation,
            });
        }

        if (outputOffset < outputFrames && !this.sourceEnded && !this.starved) {
            this.starved = true;
            this.port.postMessage({
                type: "underrun",
                generation: this.generation,
            });
        } else if (outputOffset === outputFrames) {
            this.starved = false;
        }

        return true;
    }
}

registerProcessor("light-player-pcm", LightPlayerPcmProcessor);