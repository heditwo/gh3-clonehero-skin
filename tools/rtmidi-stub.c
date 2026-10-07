/* No-device RtMidi replacement for headless test games, where ALSA sequencer access is denied. */
#include <stdbool.h>
#include <stdlib.h>

typedef struct { void *ptr; void *data; bool ok; const char *msg; } RtMidiWrapper;
typedef RtMidiWrapper *RtMidiPtr;

static RtMidiPtr create(void)
{
    RtMidiPtr device = calloc(1, sizeof(RtMidiWrapper));
    device->ptr = device;
    device->ok = true;
    device->msg = "";
    return device;
}

const char *rtmidi_get_version(void) { return "stub"; }
int rtmidi_get_compiled_api(int *apis, unsigned int size) { (void)apis; (void)size; return 0; }
const char *rtmidi_api_name(int api) { (void)api; return "dummy"; }
const char *rtmidi_api_display_name(int api) { (void)api; return "Dummy"; }
int rtmidi_compiled_api_by_name(const char *name) { (void)name; return 0; }
unsigned int rtmidi_num_compiled_apis(void) { return 0; }
const char **rtmidi_api_names(void) { static const char *none[] = { 0 }; return none; }
const int *rtmidi_compiled_apis(void) { static const int none[] = { 0 }; return none; }
void rtmidi_open_port(RtMidiPtr device, unsigned int port, const char *name) { (void)device; (void)port; (void)name; }
void rtmidi_open_virtual_port(RtMidiPtr device, const char *name) { (void)device; (void)name; }
void rtmidi_close_port(RtMidiPtr device) { (void)device; }
unsigned int rtmidi_get_port_count(RtMidiPtr device) { (void)device; return 0; }
const char *rtmidi_get_port_name(RtMidiPtr device, unsigned int port) { (void)device; (void)port; return ""; }
void rtmidi_set_error_callback(RtMidiPtr device, void *callback, void *data) { (void)device; (void)callback; (void)data; }
void rtmidi_cancel_error_callback(RtMidiPtr device) { (void)device; }
RtMidiPtr rtmidi_in_create_default(void) { return create(); }
RtMidiPtr rtmidi_in_create(int api, const char *name, unsigned int limit) { (void)api; (void)name; (void)limit; return create(); }
void rtmidi_in_free(RtMidiPtr device) { free(device); }
int rtmidi_in_get_current_api(RtMidiPtr device) { (void)device; return 0; }
void rtmidi_in_set_callback(RtMidiPtr device, void *callback, void *data) { (void)device; (void)callback; (void)data; }
void rtmidi_in_cancel_callback(RtMidiPtr device) { (void)device; }
void rtmidi_in_ignore_types(RtMidiPtr device, bool sysex, bool time, bool sense) { (void)device; (void)sysex; (void)time; (void)sense; }
double rtmidi_in_get_message(RtMidiPtr device, unsigned char *message, size_t *size) { (void)device; (void)message; if (size) *size = 0; return 0; }
RtMidiPtr rtmidi_out_create_default(void) { return create(); }
RtMidiPtr rtmidi_out_create(int api, const char *name) { (void)api; (void)name; return create(); }
void rtmidi_out_free(RtMidiPtr device) { free(device); }
int rtmidi_out_get_current_api(RtMidiPtr device) { (void)device; return 0; }
int rtmidi_out_send_message(RtMidiPtr device, const unsigned char *message, int length) { (void)device; (void)message; (void)length; return 0; }
