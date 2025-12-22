#include "ParseConfig.h"
#include "Utils.h"
play_list_context* play_list_alloc()
{
	play_list_context* temp = (play_list_context*)av_mallocz(sizeof(play_list_context));
	if (!temp) return NULL;
	return temp;
}

int play_list_add(play_list_context *list_ctx,char *key,char *value,AVDictionary *dict)
{
	if (!list_ctx || !key || !value) return -1;

	play_list* node = av_mallocz(sizeof(play_list));
	play_list* rear_node = list_ctx->rear;
	play_list* head_node = list_ctx->head;
	char* temp1 = av_mallocz(strlen(key)+1);
	char* temp2 = av_mallocz(strlen(value)+1);

	if (!node || !temp1 || !temp2)  goto failed;

	strcpy_s(temp1, strlen(key) + 1, key);
	strcpy_s(temp2, strlen(value) + 1, value);
	node->key = temp1;
	node->value = temp2;
	node->dict = dict;

	if (!head_node) {
		node->previous = node;
		node->next = node;
		list_ctx->head = node;
		list_ctx->current = node;
		list_ctx->rear = node;
	}
	else {
		rear_node->next= node;
		node->previous = rear_node;
		node->next = head_node;
		head_node->previous = node;
		list_ctx->rear = node;
	}

	list_ctx->total_size += 1;

	return 0;
failed:
	if(node) 
		av_free(node);
	if(temp1)
		av_free(temp1);
	if(temp2)
		av_free(temp2);
	return -1;
}

void play_list_free(play_list_context** list)
{
	if (!list || !*list) return;
	play_list* temp_node = NULL;
	play_list* head_node = temp_node = (*list)->head;
	
	if (!head_node) {
		av_free(*list);
		*list = NULL;
		return;
	}
	do {
		play_list* temp = temp_node->next;
		av_free(temp_node->key);
		av_free(temp_node->value);
		av_dict_free(&temp_node->dict);
		av_free(temp_node);
		temp_node = temp;
	} while (temp_node != head_node);


	
	av_free(*list);
	*list = NULL;
}


paremeter_table_context* paremeter_table_alloc(int size)
{
	if (size < 1) { return NULL; }
	paremeter_table_context *context = av_mallocz(sizeof(paremeter_table_context));
	paremeter_table*table= av_mallocz(sizeof(paremeter_table) * size);
	if (!table||!context) goto failed;


	context->table_size = size;
	context->table = table;

	return context;

failed:
	av_free(table);
	av_free(context);
	return NULL;
}

void trim_space(char* strIn, char* strOut) {

	if (!strIn || !strOut)
	{
		//printf("strinf buffer is NULL!");
		return ;
	}
	int i, j;

	i = 0;

	j = strlen(strIn) - 1;

	while (i < strlen(strIn) && strIn[i] == ' ')
		++i;


	while (j >=0 && strIn[j] == ' ')
		--j;

	if (i == strlen(strIn) && j == -1) {
		strOut[0] = '\0';
	}
	else {
		strncpy(strOut, strIn + i, (int64_t)j - i + 1);
		strOut[j - i + 1] = '\0';
	}
}


int get_paremeter_name(char* strIn,char*strOut) {

	if (!strIn || !strOut)
	{
		printf("strinf buffer is NULL!");
		return -1;
	}
	int ret=-1;
	char temp[512];
	int i = 0;

	for (i= 0; i < strlen(strIn); i++)
	{
		if (strIn[i] == '=') {
			ret = 0;
			break;
		}
		temp[i] = strIn[i];
	}
	temp[i] = '\0';

	if (ret >= 0)
	{
		trim_space(temp, strOut);
	}

	return ret;
}

int get_paremeter_content(char* strIn, char* strOut) {

	if (!strIn || !strOut)
	{
		printf("strinf buffer is NULL!");
		return -1;
	}
	int ret = -1;
	
	int i = 0;

	for (i = 0; i < strlen(strIn); i++)
	{
		if (strIn[i] == '=') {
			ret = 0;
			break;
		}
	}

	if ((i >= strlen(strIn) - 1)) {
		ret = -1;
	}

	if (ret >= 0)
	{
		trim_space(&strIn[i+1], strOut);
	}

	return ret;
}

int get_paremeter_type(char* strIn, char* strOut)
{
	if (!strIn || !strOut)
	{
		printf("strinf buffer is NULL!");
		return -1;
	}

	int i, j;

	i = 0;

	j = strlen(strIn) - 1;

	while (i <= j && strIn[i] != '[')
		++i;

	while (j >=0 && strIn[j] != ']')
		--j;
	

	if (i >= strlen(strIn) || j < 0 || j - i - 1 <0)
	{
		return -1;
	}

	strncpy(strOut, strIn + i + 1, j - i -1);
	strOut[j - i + 1] = '\0';

	return 0;
}

int update_config_from_file(char *config_file,paremeter_table_context *table_ctx)
{
	if (!config_file)
	{
		printf("config_file is NULL!");
		return -1;
	}

	if (!table_ctx)
	{
		printf("paremeter_table is NULL!");
		return -1;
	}
	char buffer[512] = { 0 };
	char name[512] = { 0 };
	char content[512] = { 0 };
	char type[128] = { 0 };
	int i = 0;
	FILE* fp = fopen(config_file, "r");
	if (!fp) {
		printf("Cannot load config file!");
		return -1;
	}

	while (fgets(buffer, sizeof(buffer), fp) != NULL) {
		trim_string(buffer, '#');
		trim_string(buffer, '\n');
		get_paremeter_type(buffer, type);

		if (i >= table_ctx->table_size) break;
		if (strlen(type) > 0 && get_paremeter_name(buffer, name) >= 0 && get_paremeter_content(buffer, content) >= 0)
		{
			char* temp1 = av_mallocz(strlen(type) + 1);
			char* temp2 = av_mallocz(strlen(name) + 1);
			char* temp3 = av_mallocz(strlen(content) + 1);

			if (!temp1 || !temp2 || !temp3)
			{
				av_free(temp1);
				av_free(temp2);
				av_free(temp3);
				continue;
			}

			strcpy_s(temp1, strlen(type) + 1, type);
			strcpy_s(temp2, strlen(name) + 1, name);
			strcpy_s(temp3, strlen(content) + 1, content);

			table_ctx->table[i++] = (paremeter_table){ temp1,temp2,temp3 };
		}
	}
	fclose(fp);
	return 0;
}

void paremeter_table_print(paremeter_table_context* table_ctx)
{
	if (!table_ctx)
	{
		printf("paremeter_table is NULL!");
		return;
	}

	for (int i = 0; i < table_ctx->table_size; i++)
	{
		if(table_ctx->table[i].type && table_ctx->table[i].name && table_ctx->table[i].content)
		printf("[%s]\t%s\t->\t%s\n", table_ctx->table[i].type, table_ctx->table[i].name, table_ctx->table[i].content);
	}
}

void paremeter_table_free(paremeter_table_context** table_ctx)
{
	if (!table_ctx || !*table_ctx)
	{
		printf("paremeter_table is NULL!");
		return;
	}
	paremeter_table * temp = (*table_ctx)->table;

	for (int i = 0; i < (*table_ctx)->table_size; i++)
	{
		if(temp[i].type)
			av_free(temp[i].type);
		if (temp[i].name)
			av_free(temp[i].name);
		if (temp[i].content)
			av_free(temp[i].content);

		/*free((*table_ctx)->table[i].type);
		free((*table_ctx)->table[i].name);
		free((*table_ctx)->table[i].content);*/
	}

	av_free(temp);
	av_free(*table_ctx);
	*table_ctx = NULL;
}

char * get_paremeter_table_content(paremeter_table_context *table_ctx,char* type, char* name, int dup_str)
{
	if (!type || !name ||!table_ctx)
	{
		printf("get_paremeter_table_content failed!\n");
		return NULL;
	}
	int i = 0;
	for (i = 0; i < table_ctx->table_size; i++)
	{
		if (!table_ctx->table[i].name || !table_ctx->table[i].type || !table_ctx->table[i].content)
			continue;

		if (!strcmp(table_ctx->table[i].name, name) && !strcmp(table_ctx->table[i].type, type)) {
			if (dup_str)
				return av_strdup(table_ctx->table[i].content);
			else
				return table_ctx->table[i].content;
		}
	}
	
	return NULL;
}

int play_list_get_size(play_list_context* list_ctx)
{
	if (!list_ctx) return -1;

	return list_ctx->total_size;
}

play_list* play_list_search(play_list_context *list_ctx,char* value)//搜索value
{
	if (!value || !list_ctx || !list_ctx->head)
	{
		//printf("value is NULL!");
		return NULL;
	}
	play_list* current_list = list_ctx->current;
	play_list* temp_list = list_ctx->current;//从当前的下一个开始找


	do {
		temp_list = temp_list->next;
		if (strstr(temp_list->value, value))
		{
			list_ctx->current = temp_list;
			return temp_list;
		}
	} while (temp_list != current_list);

	return NULL;
}


int update_play_list_from_file(play_list_context *list_ctx,char *filename)
{
	if (!list_ctx || !filename) return -1;

	FILE* fp = fopen(filename, "r");

	if (!fp) return -1;
	char number[24] = { 0 };
	char buffer[1024] = {0};
	char buffer2[1024] = {0};
	int count = 0;
	while (fgets(buffer, sizeof(buffer), fp)!=NULL)
	{
		trim_string(buffer, '\n');
		trim_space(buffer, buffer2);
		//printf(buffer2);
		if (strlen(buffer2) > 0)
		{
			printf(buffer2);
			char *tab_pos = strchr(buffer2, '\t');
			char* value = NULL;
			AVDictionary* dict = NULL;
			if (tab_pos) {
				*tab_pos = '\0';
				value = buffer2;
				av_dict_parse_string(&dict, tab_pos + 1,"=", ",",0);
			}
			else {
				value = buffer2;
			}
			sprintf(number, "%d", ++count);

			play_list_add(list_ctx, number, value, dict);
		}
	}
	fclose(fp);
	return 0;
}

play_list* play_list_seek(play_list_context* list_ctx, int seek_index)
{
	if (!list_ctx) return NULL;
	
	play_list* temp_node = list_ctx->current;
	if (!temp_node) return NULL;
	int i = seek_index;
	if (i < 0)
	{
		while (i++)
		{
			temp_node = temp_node->previous;
		}
	}
	else if (i > 0) {
		while (i--)
		{
			temp_node = temp_node->next;
		}
	}

	list_ctx->current = temp_node;
	return temp_node;
}

char* get_filter_text(char* filename)//请自行free返回结果,不完善，随便写的
{
	if (!filename)
	{
		return NULL;
	}
	FILE* fp = fopen(filename, "r");
	if (!fp) return NULL;
	char buf[1024];
	char buf2[1024] = {0};
	if (fgets(buf, sizeof(buf), fp))
	{
		trim_space(buf, buf2);
	}
	fclose(fp);
	return av_strdup(buf2);
}
//*out_ret : 1:搜索 2：移动位置 3:设置列表循环 4：单曲循环 5:随机播放   1,2,3时，要看*output内容

int control_input_playing(char* control_file,char *output,int output_size)
{
	if (!control_file) return -1;

	FILE* fp = fopen(control_file, "r");
	if (!fp) return -1;
	int ret = -1;
	char buf[512];
	char buf2[512] = { 0 };
	char* key_word[] = { "goto","go","loop_list","loop_single" ,"loop_random" };
	char* p = NULL;

	while (fgets(buf, sizeof(buf), fp))
	{
		trim_string(buf, '\n');
		trim_space(buf, buf2);
		if (strlen(buf2) > 0) break;
	}
	
	if (strlen(buf2) == 0) goto end;

	int i = 0;

	for (i = 0; i < sizeof(key_word) / sizeof(char*); i++)
	{
		if (p = strstr(buf2, key_word[i])) {
			p = strstr(buf2, " ");
			trim_space(p, buf);
			break;
		}
	}

	if (!buf) goto end;

	ret = i + 1 > sizeof(key_word) / sizeof(char*) ? -1: i + 1;

	if (ret == 1 || ret == 2)
	{
		strcpy_s(output, output_size, buf);
	}
	else if(i<sizeof(key_word) / sizeof(char*)){
		printf("play mode:%s\n", key_word[i]);
	}

	
end:
	fclose(fp);
	return ret;
}


int main05()
{
	while (1){
		char buf[512] = { 0 };
	control_input_playing("control.txt", buf,sizeof(buf));
}
	//printf("buf=%s", buf);
	return 0;
	
}