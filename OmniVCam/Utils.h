#define _CRT_SECURE_NO_WARNINGS
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <stdint.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <libavutil/mem.h>
#include <libavutil/time.h>
#include <stdlib.h>

#define CONFIG_ROOT_ENV "OMNI_VCAM_CONFIG"

#ifdef _WIN32
#define PATH_SEPARATOR '\\'
#define PATH_SEPARATOR_STR "\\"
#else
#define PATH_SEPARATOR '/'
#define PATH_SEPARATOR_STR "/"
#endif

char* get_time_string(char* time_str, int size);

int generate_random_number(int max_number);

int trim_url_name(char* in, char* out);

int write_text_to_file(char* filename, char* text, char* mode);


int64_t is_file_changed(char* filename, int64_t last_mtime);

char* join_path(char* env_value, char* filename);

char* trim_string(char* buffer, char token);